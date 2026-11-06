using System.IO;
using System.Text;

namespace JeonCheck.Services;

// 코드 뷰어용 소스 파일 인코딩 판별·읽기 도우미.
// 자동 판별은 BOM(UTF-8/UTF-16/UTF-32) 우선, BOM이 없으면 엄격한 UTF-8 검증을 시도하고
// 실패하면 CP949로 읽는다. CP949는 EUC-KR의 상위 집합이라 EUC-KR 파일도 깨지지 않고 표시된다.
public static class SourceFileEncoding
{
    // 상태 표시줄 버튼으로 순환 전환하는 인코딩 목록.
    public static IReadOnlyList<string> SupportedNames { get; } = new[] { "UTF-8", "CP949", "EUC-KR" };

    public sealed record ReadResult(string[] Lines, string EncodingName);

    static SourceFileEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static ReadResult ReadAllLines(string path, string? encodingName = null)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string name = encodingName ?? DetectName(bytes);
        Encoding encoding = GetEncoding(name);
        int offset = PreambleLength(bytes, name);
        string text = encoding.GetString(bytes, offset, bytes.Length - offset);
        return new ReadResult(SplitLines(text), name);
    }

    public static string DetectName(byte[] bytes)
    {
        foreach (var (preamble, name) in KnownPreambles)
        {
            if (StartsWith(bytes, preamble))
                return name;
        }

        try
        {
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return "UTF-8";
        }
        catch (DecoderFallbackException)
        {
            return "CP949";
        }
    }

    public static Encoding GetEncoding(string name) => name switch
    {
        "CP949" => Encoding.GetEncoding(949),
        "EUC-KR" => Encoding.GetEncoding("euc-kr"),
        "UTF-16 LE" => Encoding.Unicode,
        "UTF-16 BE" => Encoding.BigEndianUnicode,
        "UTF-32 LE" => Encoding.UTF32,
        "UTF-32 BE" => new UTF32Encoding(bigEndian: true, byteOrderMark: false),
        _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
    };

    private static readonly (byte[] Preamble, string Name)[] KnownPreambles = new[]
    {
        (new byte[] { 0xEF, 0xBB, 0xBF }, "UTF-8"),
        (new byte[] { 0xFF, 0xFE, 0x00, 0x00 }, "UTF-32 LE"),
        (new byte[] { 0x00, 0x00, 0xFE, 0xFF }, "UTF-32 BE"),
        (new byte[] { 0xFF, 0xFE }, "UTF-16 LE"),
        (new byte[] { 0xFE, 0xFF }, "UTF-16 BE"),
    };

    private static int PreambleLength(byte[] bytes, string name)
    {
        foreach (var (preamble, preambleName) in KnownPreambles)
        {
            if (preambleName == name && StartsWith(bytes, preamble))
                return preamble.Length;
        }

        return 0;
    }

    private static bool StartsWith(byte[] bytes, byte[] preamble)
    {
        if (bytes.Length < preamble.Length)
            return false;

        for (int i = 0; i < preamble.Length; i++)
        {
            if (bytes[i] != preamble[i])
                return false;
        }

        return true;
    }

    // File.ReadAllLines와 같은 줄 나눔 의미: 줄바꿈 문자(\r\n, \n, \r)로 나누고
    // 파일 끝의 줄바꿈 뒤에 생기는 빈 조각은 한 줄로 세지 않는다.
    private static string[] SplitLines(string text)
    {
        if (text.Length == 0)
            return Array.Empty<string>();

        string[] lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        if (lines.Length > 0 && lines[^1].Length == 0 && (text.EndsWith('\n') || text.EndsWith('\r')))
            Array.Resize(ref lines, lines.Length - 1);

        return lines;
    }
}
