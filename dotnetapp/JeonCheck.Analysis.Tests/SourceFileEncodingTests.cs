using System.Text;
using JeonCheck.Services;

static class SourceFileEncodingTests
{
    public static void Run(Action<string, Action> test)
    {
        test("UTF-8 한글 바이트는 UTF-8로 판별하고 그대로 읽음", () =>
        {
            const string text = "int 변수 = 1; // 한글 주석";
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            Require(SourceFileEncoding.DetectName(bytes) == "UTF-8", "UTF-8 판별 실패");
            Require(ReadTemp(bytes, null) is { Lines: [{ } first], EncodingName: "UTF-8" } && first == text, "UTF-8 읽기 불일치");
        });

        test("CP949 한글 바이트는 UTF-8이 아님을 판별하고 CP949로 복원", () =>
        {
            const string text = "int 변수 = 1; // 한글 주석";
            byte[] bytes = Encoding.GetEncoding(949).GetBytes(text);
            Require(SourceFileEncoding.DetectName(bytes) == "CP949", "CP949 판별 실패");
            try
            {
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
                throw new Exception("기존 UTF-8 고정 읽기는 이 바이트에서 실패해야 함");
            }
            catch (DecoderFallbackException) { }
            Require(ReadTemp(bytes, null) is { Lines: [{ } first], EncodingName: "CP949" } && first == text, "CP949 복원 실패");
        });

        test("UTF-8 BOM은 UTF-8로 판별하고 BOM을 제거", () =>
        {
            const string text = "int 변수 = 1;";
            byte[] bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(text)).ToArray();
            var result = ReadTemp(bytes, null);
            Require(result.EncodingName == "UTF-8", "BOM 판별 실패");
            Require(result.Lines.Length == 1 && result.Lines[0] == text, "BOM 제거 실패");
        });

        test("EUC-KR 지정 읽기는 한글을 복원", () =>
        {
            const string text = "한글 주석";
            byte[] bytes = Encoding.GetEncoding("euc-kr").GetBytes(text);
            var result = ReadTemp(bytes, "EUC-KR");
            Require(result.EncodingName == "EUC-KR", "EUC-KR 이름 보존 실패");
            Require(result.Lines.Length == 1 && result.Lines[0] == text, "EUC-KR 복원 실패");
        });

        test("줄 나눔은 File.ReadAllLines와 같은 의미", () =>
        {
            var result = ReadTemp(Encoding.UTF8.GetBytes("a\r\nb\n"), null);
            Require(result.Lines.SequenceEqual(new[] { "a", "b" }), "끝 줄바꿈 처리 불일치");
            var empty = ReadTemp(Array.Empty<byte>(), null);
            Require(empty.Lines.Length == 0, "빈 파일 처리 불일치");
        });
    }

    private static SourceFileEncoding.ReadResult ReadTemp(byte[] bytes, string? encodingName)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, bytes);
            return SourceFileEncoding.ReadAllLines(path, encodingName);
        }
        finally { File.Delete(path); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
