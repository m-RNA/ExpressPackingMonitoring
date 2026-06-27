namespace ExpressPackingMonitoring
{
    public sealed class MkvConversionResult
    {
        public bool Success { get; init; }
        public string FilePath { get; init; } = "";
        public string ErrorMessage { get; init; } = "";
        public bool AlreadyConverted { get; init; }

        public static MkvConversionResult Ok(string filePath, bool alreadyConverted = false) =>
            new() { Success = true, FilePath = filePath, AlreadyConverted = alreadyConverted };

        public static MkvConversionResult Fail(string errorMessage, string filePath = "") =>
            new() { Success = false, ErrorMessage = errorMessage, FilePath = filePath };
    }
}
