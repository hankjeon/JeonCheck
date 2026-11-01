namespace JeonCheck.Models;

public class DumpPlatform
{
    public int CharBit { get; set; } = 8;
    public int ShortBit { get; set; } = 16;
    public int IntBit { get; set; } = 32;
    public int LongBit { get; set; } = 32;
    public int LongLongBit { get; set; } = 64;
    public int FloatBit { get; set; } = 32;
    public int DoubleBit { get; set; } = 64;
    public int LongDoubleBit { get; set; } = 64;
    public int PointerBit { get; set; } = 32;

    public int GetBitWidth(string? valueType, int pointerLevel)
    {
        if (pointerLevel > 0)
            return PointerBit;

        return valueType switch
        {
            "bool" => 1,
            "char" => CharBit,
            "short" => ShortBit,
            "int" => IntBit,
            "long" => LongBit,
            "long long" => LongLongBit,
            "float" => FloatBit,
            "double" => DoubleBit,
            "long double" => LongDoubleBit,
            _ => 0
        };
    }

    public static bool IsIntegerType(string? valueType, int pointerLevel)
    {
        if (pointerLevel > 0)
            return false;
        return valueType is "char" or "short" or "int" or "long" or "long long";
    }

    public static bool IsFloatType(string? valueType, int pointerLevel)
    {
        if (pointerLevel > 0)
            return false;
        return valueType is "float" or "double" or "long double";
    }
}

public class DumpToken
{
    public string Id { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public int LineNr { get; set; }
    public int Column { get; set; }
    public string Str { get; set; } = string.Empty;
    public string ScopeId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string LinkId { get; set; } = string.Empty;
    public string VariableId { get; set; } = string.Empty;
    public string FunctionId { get; set; } = string.Empty;
    public string AstParentId { get; set; } = string.Empty;
    public string AstOperand1Id { get; set; } = string.Empty;
    public string AstOperand2Id { get; set; } = string.Empty;

    public bool IsAssignmentOp { get; set; }
    public bool IsComparisonOp { get; set; }
    public bool IsArithmeticalOp { get; set; }
    public bool IsCast { get; set; }
    public bool IsInsertedBrace { get; set; }
    public bool IsSplittedVarDeclEq { get; set; }
    public bool IsIntLiteral { get; set; }
    public bool IsFloatLiteral { get; set; }
    public bool IsSigned { get; set; }   // 타입 이름 토큰에 'signed'가 명시됨 (예: signed char)
    public bool IsUnsigned { get; set; } // 타입 이름 토큰에 'unsigned'가 명시됨 (예: unsigned char, uint8_t)
    public bool IsRemovedVoidParameter { get; set; } // 함수 선언의 '(void)'에서 void가 제거된 '(' 토큰
    public string VarId { get; set; } = string.Empty;
    public string MacroName { get; set; } = string.Empty; // 매크로 전개로 생긴 토큰의 원래 매크로 이름 (예: false → 0)

    public string? ValueType { get; set; }
    public string? ValueSign { get; set; } // "signed", "unsigned"
    public int ValuePointer { get; set; }
    public int ValueConstness { get; set; }
    public int ValueVolatileness { get; set; }
    public string ValueTypeScopeId { get; set; } = string.Empty;
    public string? OriginalTypeName { get; set; }

    public DumpToken? Next { get; set; }
    public DumpToken? Previous { get; set; }
    public DumpToken? Link { get; set; }
    public DumpToken? AstParent { get; set; }
    public DumpToken? AstOperand1 { get; set; }
    public DumpToken? AstOperand2 { get; set; }
    public DumpVariable? Variable { get; set; }

    public string FormatTypeDisplay(DumpPlatform platform)
    {
        if (ValuePointer > 0)
        {
            string stars = new string('*', ValuePointer);
            string baseName = !string.IsNullOrEmpty(OriginalTypeName) ? OriginalTypeName : (ValueType ?? "void");
            return $"{baseName}{stars} ({platform.PointerBit}-bit ptr)";
        }

        if (string.IsNullOrEmpty(ValueType))
            return "unknown";

        int bits = platform.GetBitWidth(ValueType, 0);
        string signPrefix = ValueSign == "unsigned" ? "unsigned " : "";
        string typeStr = $"{signPrefix}{ValueType}".Trim();
        return bits > 0 ? $"{typeStr} ({bits}-bit)" : typeStr;
    }
}

public class DumpScope
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // Global, Function, If, Else, For, While, Switch
    public string ClassName { get; set; } = string.Empty;
    public string BodyStartId { get; set; } = string.Empty;
    public string BodyEndId { get; set; } = string.Empty;
    public string NestedInId { get; set; } = string.Empty;
    public string FunctionId { get; set; } = string.Empty;
}

public class DumpVariable
{
    public string Id { get; set; } = string.Empty;
    public string NameTokenId { get; set; } = string.Empty;
    public string TypeStartTokenId { get; set; } = string.Empty;
    public string TypeEndTokenId { get; set; } = string.Empty;
    public string Access { get; set; } = string.Empty; // Local, Argument, Global, Public
    public string ScopeId { get; set; } = string.Empty;
    public bool IsPointer { get; set; }
    public bool IsArray { get; set; }
    public bool IsConst { get; set; }
    public bool IsVolatile { get; set; }
    public bool IsStatic { get; set; }
    public int Constness { get; set; }
    public int Volatileness { get; set; }
    public DumpToken? NameToken { get; set; }
    public DumpToken? TypeStartToken { get; set; }
}

/// <summary>
/// 함수 정보 (dump의 scope/functionList/function). Token은 함수 정의(본문) 위치, TokenDef는 선언 위치
/// </summary>
public class DumpFunction
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TokenId { get; set; } = string.Empty;
    public string TokenDefId { get; set; } = string.Empty;
    public List<string> ArgVariableIds { get; set; } = new();

    public DumpToken? Token { get; set; }
    public DumpToken? TokenDef { get; set; }
    public List<DumpVariable?> Args { get; set; } = new();
}

public class DumpDirective
{
    public string File { get; set; } = string.Empty;
    public int LineNr { get; set; }
    public string Str { get; set; } = string.Empty;
}

public class DumpCfgUnit
{
    public string CfgName { get; set; } = string.Empty;
    public List<DumpToken> Tokens { get; set; } = new();
    public Dictionary<string, DumpToken> TokenById { get; set; } = new();
    public Dictionary<string, DumpScope> ScopeById { get; set; } = new();
    public Dictionary<string, DumpVariable> VariableById { get; set; } = new();
    public Dictionary<string, DumpFunction> FunctionById { get; set; } = new();
    public List<DumpDirective> Directives { get; set; } = new();
}

public class FileLineCoverage
{
    public string FilePath { get; set; } = string.Empty;
    public HashSet<int> AnalyzedLines { get; set; } = new();
    public HashSet<int> SkippedLines { get; set; } = new();
    public HashSet<int> DirectiveLines { get; set; } = new();
}

public class DumpDocument
{
    public DumpPlatform Platform { get; set; } = new();
    public List<DumpCfgUnit> Configurations { get; set; } = new();
    public Dictionary<string, FileLineCoverage> CoverageByFile { get; set; } = new();
}
