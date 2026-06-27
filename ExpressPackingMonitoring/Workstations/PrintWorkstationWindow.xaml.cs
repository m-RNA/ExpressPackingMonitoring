using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using ExpressPackingMonitoring.ViewModels;

namespace ExpressPackingMonitoring;

public partial class PrintWorkstationWindow : Window
{
    private readonly AppConfig _config;
    private readonly string _activeWorkstationRole = WorkstationRoles.PrintStation;
    private CancellationTokenSource? _findCts;

    public PrintWorkstationWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        AddressTextBox.Text = WorkstationNetwork.NormalizeAddress(_config.PrintStationMonitorAddress);
        Loaded += async (_, __) => await AutoConnectAndOpenAsync();
    }

    private async Task<bool> ReconnectAsync(bool save, bool openWhenConnected = false)
    {
        string address = WorkstationNetwork.NormalizeAddress(AddressTextBox.Text);
        AddressTextBox.Text = address;
        if (string.IsNullOrWhiteSpace(address))
        {
            SetStatus("未连接摄像头监控工位", "请自动查找，或输入摄像头监控工位底部状态栏显示的地址。");
            return false;
        }

        SetStatus("正在连接摄像头监控工位...", address);
        bool ok = await WorkstationNetwork.CanConnectAsync(address);
        if (ok)
        {
            if (save)
            {
                _config.PrintStationMonitorAddress = address;
                WorkstationConfigStore.Save(_config);
            }
            SetStatus("已连接到摄像头监控工位", $"已记住地址：{address}");
            if (openWhenConnected)
                WorkstationNetwork.OpenUrl(WorkstationNetwork.ToUrl(address));
            return true;
        }
        else
        {
            SetStatus("未找到摄像头监控工位", "请确认摄像头监控工位已打开，并且两台电脑在同一局域网。");
            return false;
        }
    }

    private async Task AutoConnectAndOpenAsync()
    {
        string savedAddress = WorkstationNetwork.NormalizeAddress(_config.PrintStationMonitorAddress);
        if (!string.IsNullOrWhiteSpace(savedAddress))
        {
            AddressTextBox.Text = savedAddress;
            if (await ReconnectAsync(true, openWhenConnected: true))
                return;
        }

        await FindMonitorAsync(openWhenFound: true);
    }

    private void SetStatus(string title, string hint)
    {
        StatusTextBlock.Text = title;
        StatusHintTextBlock.Text = hint;
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e) => await ReconnectAsync(true, openWhenConnected: true);

    private async void FindMonitor_Click(object sender, RoutedEventArgs e)
    {
        await FindMonitorAsync(openWhenFound: true);
    }

    private async Task FindMonitorAsync(bool openWhenFound)
    {
        _findCts?.Cancel();
        _findCts = new CancellationTokenSource();
        SetStatus("正在自动查找摄像头监控工位...", "查找过程可能需要几十秒。");
        var progress = new Progress<string>(msg => StatusHintTextBlock.Text = msg);

        try
        {
            string? address = await WorkstationNetwork.FindMonitorAsync(_config.WebServerPort, progress, _findCts.Token);
            if (address == null)
            {
                SetStatus("未找到摄像头监控工位", "请手动输入摄像头监控工位底部状态栏显示的地址。");
                return;
            }

            AddressTextBox.Text = address;
            await ReconnectAsync(true, openWhenConnected: openWhenFound);
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消查找", "可以重新点击自动查找。");
        }
    }

    private void OpenWeb_Click(object sender, RoutedEventArgs e)
    {
        string address = WorkstationNetwork.NormalizeAddress(AddressTextBox.Text);
        if (string.IsNullOrWhiteSpace(address))
        {
            SetStatus("未连接摄像头监控工位", "请先连接后再打开视频页面。");
            return;
        }
        WorkstationNetwork.OpenUrl(WorkstationNetwork.ToUrl(address));
    }

    private void InstallTool_Click(object sender, RoutedEventArgs e)
    {
        string address = WorkstationNetwork.NormalizeAddress(AddressTextBox.Text);
        string guidePath = CreateInstallGuide(address);
        if (!string.IsNullOrWhiteSpace(address))
        {
            try { Clipboard.SetDataObject(address, true); } catch { }
        }

        WorkstationNetwork.OpenUrl(new Uri(guidePath).AbsoluteUri);
        SetStatus("已打开安装向导", string.IsNullOrWhiteSpace(address)
            ? "请先连接摄像头监控工位，再按向导安装联动工具。"
            : $"已复制监控工位地址：{address}");
    }

    private static string ResolveUserscriptPath()
    {
        string[] candidates =
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Scripts", "快递助手订单推送.user.js")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Scripts", "快递助手订单推送.user.js")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Scripts", "快递助手订单推送.user.js"))
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static string CreateInstallGuide(string monitorAddress)
    {
        string guideDir = Path.Combine(AppPaths.LogDir, "guide");
        Directory.CreateDirectory(guideDir);
        string guidePath = Path.Combine(guideDir, "kuaidizs-install-guide.html");
        string scriptPath = ResolveUserscriptPath();
        string scriptUrl = File.Exists(scriptPath) ? new Uri(scriptPath).AbsoluteUri : "";
        string address = WebUtility.HtmlEncode(monitorAddress);
        string scriptLink = string.IsNullOrWhiteSpace(scriptUrl)
            ? "<div class=\"warn\">未找到联动脚本文件，请确认发布包内包含 Scripts 文件夹。</div>"
            : $"<a class=\"primary\" href=\"{WebUtility.HtmlEncode(scriptUrl)}\">打开联动脚本安装页</a>";

        string html = $$"""
<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>快递助手联动工具安装向导</title>
  <style>
    body{font-family:"Microsoft YaHei UI","Segoe UI",sans-serif;margin:0;background:#f5f7fb;color:#172033}
    main{max-width:900px;margin:28px auto;padding:0 20px}
    .card{background:#fff;border:1px solid #d8e0ec;border-radius:12px;padding:24px;box-shadow:0 10px 30px rgba(26,40,70,.08)}
    h1{font-size:26px;margin:0 0 8px}
    p{line-height:1.7;color:#516071}
    .steps{display:grid;gap:14px;margin-top:22px;counter-reset:step}
    .step{border:1px solid #e3e8f0;border-radius:10px;padding:16px;background:#fbfcff;display:grid;grid-template-columns:42px 1fr;gap:14px}
    .num{width:34px;height:34px;border-radius:50%;background:#1f78ff;color:#fff;display:flex;align-items:center;justify-content:center;font-weight:800}
    .step h2{font-size:18px;margin:3px 0 8px}
    .actions{display:flex;flex-wrap:wrap;gap:10px;margin-top:10px}
    a.primary,button{display:inline-flex;align-items:center;justify-content:center;min-height:40px;padding:0 16px;border-radius:8px;border:0;background:#1f78ff;color:#fff;text-decoration:none;font-weight:700;cursor:pointer}
    a.secondary,button.secondary{display:inline-flex;align-items:center;justify-content:center;min-height:40px;padding:0 16px;border-radius:8px;border:1px solid #ccd6e4;color:#172033;text-decoration:none;font-weight:700;background:#fff}
    code{font-size:18px;background:#eef4ff;border:1px solid #cdddf5;border-radius:8px;padding:10px 12px;display:inline-block;color:#0c4fb3;margin-right:10px}
    .warn{color:#a44800;background:#fff6e8;border:1px solid #f0c98e;border-radius:8px;padding:12px}
    .hint{font-size:13px;color:#6b7788;margin-top:10px}
    .check{margin:8px 0 0 0;padding-left:20px;color:#39475a;line-height:1.7}
  </style>
</head>
<body>
<main>
  <div class="card">
    <h1>快递助手联动工具安装向导</h1>
    <p>按下面步骤操作。安装完成后，在快递助手打印订单时，订单信息会自动发送到摄像头监控工位。</p>
    <div class="steps">
      <div class="step">
        <div class="num">1</div>
        <div>
          <h2>安装 Tampermonkey / 篡改猴</h2>
          <p>先给浏览器安装脚本管理扩展。如果已经安装过，可以跳过这一步。</p>
          <div class="actions">
            <a class="primary" href="https://chromewebstore.google.com/detail/tampermonkey/dhdgffkkebhmkfjojejmpbldmpobfkfo">Chrome 安装</a>
            <a class="secondary" href="https://microsoftedge.microsoft.com/addons/detail/tampermonkey/iikmkjmpaadaobahmlepeloendndfphd">Edge 安装</a>
            <a class="secondary" href="https://www.tampermonkey.net/">其他浏览器</a>
          </div>
        </div>
      </div>
      <div class="step">
        <div class="num">2</div>
        <div>
          <h2>打开浏览器的用户脚本权限</h2>
          <p>Chrome / Edge 新版本需要在扩展详情里打开“允许用户脚本”；如果没有这个开关，就打开“开发者模式”。本地安装脚本时，也建议打开“允许访问文件网址”。</p>
          <ul class="check">
            <li>打开浏览器扩展管理页面，找到 Tampermonkey / 篡改猴。</li>
            <li>进入“详情”，打开“允许用户脚本”。</li>
            <li>如果没有“允许用户脚本”，打开“开发者模式”：Edge 一般在左下角，Chrome 一般在右上角。</li>
            <li>如果有“允许访问文件网址”，也一起打开。</li>
          </ul>
          <div class="actions">
            <button class="secondary" onclick="copyText('chrome://extensions/','已复制 Chrome 扩展页地址')">复制 Chrome 扩展页地址</button>
            <button class="secondary" onclick="copyText('edge://extensions/','已复制 Edge 扩展页地址')">复制 Edge 扩展页地址</button>
            <a class="secondary" href="https://www.tampermonkey.net/faq.php?locale=zh&q=Q209">查看图文说明</a>
          </div>
          <div class="hint" id="copyHint">复制后粘贴到浏览器地址栏打开。浏览器通常会拦截网页直接打开 chrome:// 或 edge:// 地址。</div>
        </div>
      </div>
      <div class="step">
        <div class="num">3</div>
        <div>
          <h2>安装快递助手联动脚本</h2>
          <p>请点击下面按钮，让 Tampermonkey 打开脚本安装页。不要用记事本、Visual Studio 或脚本宿主打开这个文件。</p>
          <div class="actions">{{scriptLink}}</div>
          <div class="hint">如果浏览器只显示代码，请确认第 1 步已安装 Tampermonkey，并完成第 2 步的权限开关。</div>
        </div>
      </div>
      <div class="step">
        <div class="num">4</div>
        <div>
          <h2>填写摄像头监控工位地址</h2>
          <p>程序已尝试把这个地址复制到剪贴板。脚本里要求填写地址时，直接粘贴即可。</p>
          <code id="addr">{{address}}</code>
          <button onclick="copyText(document.getElementById('addr').textContent,'已复制摄像头监控工位地址')">复制地址</button>
          <div class="hint">如果这里为空，请先回到打印工位窗口连接摄像头监控工位。</div>
        </div>
      </div>
    </div>
  </div>
</main>
<script>
function copyText(text, message) {
  var hint = document.getElementById('copyHint');
  function done(ok) {
    if (hint) hint.textContent = ok ? message + '，请粘贴到浏览器地址栏。' : '复制失败，请手动输入：' + text;
  }
  if (!text) {
    done(false);
    return;
  }
  if (navigator.clipboard && navigator.clipboard.writeText) {
    navigator.clipboard.writeText(text).then(function(){ done(true); }, function(){ fallbackCopy(text, done); });
    return;
  }
  fallbackCopy(text, done);
}
function fallbackCopy(text, done) {
  var input = document.createElement('textarea');
  input.value = text;
  input.style.position = 'fixed';
  input.style.opacity = '0';
  document.body.appendChild(input);
  input.focus();
  input.select();
  var ok = false;
  try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
  document.body.removeChild(input);
  done(ok);
}
</script>
</body>
</html>
""";
        File.WriteAllText(guidePath, html, Encoding.UTF8);
        return guidePath;
    }

    private async void TestSend_Click(object sender, RoutedEventArgs e)
    {
        string address = WorkstationNetwork.NormalizeAddress(AddressTextBox.Text);
        SetStatus("正在测试发送订单...", address);
        bool ok = await WorkstationNetwork.SendTestOrderAsync(address);
        SetStatus(ok ? "测试订单已发送" : "测试发送失败", ok ? "请到摄像头监控工位查看是否收到测试订单。" : "请先确认地址和网络连接。");
    }

    private void SwitchWorkstation_Click(object sender, RoutedEventArgs e)
    {
        var win = new WorkstationSelectionWindow { Owner = this };
        if (win.ShowDialog() == true && !string.IsNullOrWhiteSpace(win.SelectedRole))
        {
            if (string.Equals(_activeWorkstationRole, win.SelectedRole, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(_config.WorkstationRole, _activeWorkstationRole, StringComparison.OrdinalIgnoreCase))
                {
                    _config.WorkstationRole = _activeWorkstationRole;
                    WorkstationConfigStore.Save(_config);
                }
                SetStatus($"当前已经是{WorkstationRoles.GetDisplayName(_activeWorkstationRole)}", "无需重启或切换。");
                return;
            }

            _config.WorkstationRole = win.SelectedRole;
            WorkstationConfigStore.Save(_config);
            WorkstationNetwork.AskRestart(this);
        }
    }
}
