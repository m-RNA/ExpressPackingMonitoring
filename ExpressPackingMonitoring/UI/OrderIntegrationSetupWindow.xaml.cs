using ExpressPackingMonitoring.Config;
using System.Collections.ObjectModel;
using System.Windows;

namespace ExpressPackingMonitoring.UI;

public partial class OrderIntegrationSetupWindow : Window
{
    private readonly AppConfig _config;
    private readonly ObservableCollection<OrderIntegrationTarget> _targets;
    public IReadOnlyList<OrderIntegrationTarget> ResultTargets { get; private set; } = Array.Empty<OrderIntegrationTarget>();

    public OrderIntegrationSetupWindow(AppConfig config)
    {
        _config = config;
        InitializeComponent();
        LocalTargetCheckBox.IsChecked = config.OrderIntegrationTargets.Any(t => t.IsLocal && t.Enabled)
            || config.OrderIntegrationTargets.Count == 0;
        _targets = new ObservableCollection<OrderIntegrationTarget>(config.OrderIntegrationTargets
            .Where(t => !t.IsLocal)
            .Select(Clone));
        TargetsGrid.ItemsSource = _targets;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var target = new OrderIntegrationTarget { DisplayName = $"录像电脑 {_targets.Count + 1}", Enabled = true };
        _targets.Add(target);
        TargetsGrid.SelectedItem = target;
        TargetsGrid.ScrollIntoView(target);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (TargetsGrid.SelectedItem is OrderIntegrationTarget target) _targets.Remove(target);
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        TargetsGrid.CommitEdit();
        if (TargetsGrid.SelectedItem is not OrderIntegrationTarget target || string.IsNullOrWhiteSpace(target.Address))
        {
            TestResultText.Text = "请先选择并填写目标地址";
            return;
        }
        TestResultText.Text = "正在测试...";
        bool ok = await WorkstationNetwork.CanConnectAsync(target.Address);
        TestResultText.Text = ok ? "连接成功" : "未连接，请检查地址和防火墙";
    }

    private void InstallScript_Click(object sender, RoutedEventArgs e)
    {
        TargetsGrid.CommitEdit();
        var addresses = new List<string>();
        if (LocalTargetCheckBox.IsChecked == true)
            addresses.Add(WorkstationNetwork.GetBestLocalAccessAddress(_config.WebServerPort));
        addresses.AddRange(_targets.Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Address)).Select(t => t.Address));
        string guidePath = PrintToolInstallGuide.CreateLocalGuide(string.Join(',', addresses));
        WorkstationNetwork.OpenUrl(new Uri(guidePath).AbsoluteUri);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        TargetsGrid.CommitEdit();
        var result = _targets
            .Where(t => !string.IsNullOrWhiteSpace(t.Address))
            .Select(Clone)
            .ToList();
        if (LocalTargetCheckBox.IsChecked == true)
        {
            result.Insert(0, new OrderIntegrationTarget
            {
                DisplayName = "当前电脑",
                Address = "local",
                Enabled = true,
                IsLocal = true
            });
        }
        if (result.Count == 0)
        {
            MessageBox.Show(this, "请至少启用当前电脑或添加一个远程目标", "无法保存", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ResultTargets = result;
        DialogResult = true;
    }

    private static OrderIntegrationTarget Clone(OrderIntegrationTarget source) => new()
    {
        Id = string.IsNullOrWhiteSpace(source.Id) ? Guid.NewGuid().ToString("N") : source.Id,
        DisplayName = source.DisplayName,
        Address = source.Address,
        Enabled = source.Enabled,
        IsLocal = source.IsLocal
    };
}
