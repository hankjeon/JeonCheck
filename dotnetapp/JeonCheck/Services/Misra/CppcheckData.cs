using System.IO;
using System.Xml;

namespace JeonCheck.Services.Misra;

// Cppcheck 2.22 addons/cppcheckdata.py의 데이터 모델과 dump 해석을 C#으로 옮긴 것이다.
// misra.py 포팅(MisraChecker)이 원본과 같은 결과를 내도록 속성·연결 규칙을 원본과 동일하게 유지한다.

/// <summary>위치 정보가 있는 dump 요소 (Location, Directive, TypedefInfo 등 공통)</summary>
public class DumpLocation
{
    public string? File { get; set; }
    public int Linenr { get; set; }
    public int Column { get; set; }

    internal void LoadLocation(XmlReader r)
    {
        File = r.GetAttribute("file");
        string? line = r.GetAttribute("line") ?? r.GetAttribute("linenr") ?? "0";
        Linenr = int.Parse(line);
        Column = int.Parse(r.GetAttribute("column") ?? "0");
    }
}

public sealed class Directive : DumpLocation
{
    public string Str { get; set; } = "";
}

public sealed class MacroUsage : DumpLocation
{
    public string? Name { get; set; }
    public string? UseFile { get; set; }
    public string? UseLine { get; set; }
    public string? UseColumn { get; set; }
    public bool IsKnownValue { get; set; }
}

public sealed class PreprocessorIfCondition : DumpLocation
{
    public string? E { get; set; }
    public int Result { get; set; }
}

public sealed class ValueType
{
    public string? Type { get; set; }
    public string? Sign { get; set; }
    public int? Bits { get; set; }
    public int Pointer { get; set; }
    public int Constness { get; set; }
    public string? Reference { get; set; }
    public string? TypeScopeId { get; set; }
    public Scope? TypeScope { get; set; }
    public string? OriginalTypeName { get; set; }

    public bool IsIntegral() => Type is "bool" or "char" or "short" or "int" or "long" or "long long";
    public bool IsFloat() => Type is "float" or "double" or "long double";
    public bool IsEnum() => TypeScope != null && TypeScope.Type == "Enum";
}

public sealed class Token : DumpLocation
{
    public string? Id { get; set; }
    public string Str { get; set; } = "";
    public Token? Next { get; set; }
    public Token? Previous { get; set; }
    public string? LinkId { get; set; }
    public Token? Link { get; set; }
    public string? ScopeId { get; set; }
    public Scope? Scope { get; set; }
    public string? Type { get; set; }
    public bool IsName { get; set; }
    public bool IsNumber { get; set; }
    public bool IsInt { get; set; }
    public bool IsFloat { get; set; }
    public bool IsString { get; set; }
    public int? Strlen { get; set; }
    public bool IsChar { get; set; }
    public bool IsBoolean { get; set; }
    public bool IsOp { get; set; }
    public bool IsArithmeticalOp { get; set; }
    public bool IsAssignmentOp { get; set; }
    public bool IsComparisonOp { get; set; }
    public bool IsLogicalOp { get; set; }
    public bool IsCast { get; set; }
    public bool IsUnsigned { get; set; }
    public bool IsSigned { get; set; }
    public string? ExternLang { get; set; }
    public string? MacroName { get; set; }
    public bool IsExpandedMacro { get; set; }
    public bool IsRemovedVoidParameter { get; set; }
    public bool IsSplittedVarDeclComma { get; set; }
    public bool IsSplittedVarDeclEq { get; set; }
    public bool IsImplicitInt { get; set; }
    public bool IsComplex { get; set; }
    public bool IsRestrict { get; set; }
    public bool IsAttributeExport { get; set; }
    public bool IsAnonymous { get; set; }
    public int? VarId { get; set; }
    public int? ExprId { get; set; }
    public string? VariableId { get; set; }
    public Variable? Variable { get; set; }
    public string? FunctionId { get; set; }
    public Function? Function { get; set; }
    public string? ValuesId { get; set; }
    /// <summary>impossible 값을 제외한 값 목록. 원본처럼 setId 이후에는 null이 아닌 리스트이다.</summary>
    public List<Value>? Values { get; set; }
    public List<Value>? ImpossibleValues { get; set; }
    public ValueType? ValueType { get; set; }
    public string? TypeScopeId { get; set; }
    public Scope? TypeScope { get; set; }
    public string? AstParentId { get; set; }
    public Token? AstParent { get; set; }
    public string? AstOperand1Id { get; set; }
    public Token? AstOperand1 { get; set; }
    public string? AstOperand2Id { get; set; }
    public Token? AstOperand2 { get; set; }
    public string? OriginalName { get; set; }

    internal static Token FromXml(XmlReader r)
    {
        var t = new Token
        {
            Id = r.GetAttribute("id"),
            Str = r.GetAttribute("str") ?? "",
            ScopeId = r.GetAttribute("scope"),
            Type = r.GetAttribute("type"),
        };
        bool Has(string name) => !string.IsNullOrEmpty(r.GetAttribute(name));
        switch (t.Type)
        {
            case "name":
                t.IsName = true;
                if (Has("isUnsigned")) t.IsUnsigned = true;
                if (Has("isSigned")) t.IsSigned = true;
                break;
            case "number":
                t.IsNumber = true;
                if (Has("isInt")) t.IsInt = true;
                else if (Has("isFloat")) t.IsFloat = true;
                break;
            case "string":
                t.IsString = true;
                t.Strlen = int.Parse(r.GetAttribute("strlen")!);
                break;
            case "char":
                t.IsChar = true;
                break;
            case "boolean":
                t.IsBoolean = true;
                break;
            case "op":
                t.IsOp = true;
                if (Has("isArithmeticalOp")) t.IsArithmeticalOp = true;
                else if (Has("isAssignmentOp")) t.IsAssignmentOp = true;
                else if (Has("isComparisonOp")) t.IsComparisonOp = true;
                else if (Has("isLogicalOp")) t.IsLogicalOp = true;
                break;
        }
        if (Has("isCast")) t.IsCast = true;
        t.ExternLang = r.GetAttribute("externLang");
        t.MacroName = r.GetAttribute("macroName");
        if (!string.IsNullOrEmpty(t.MacroName) || Has("isExpandedMacro")) t.IsExpandedMacro = true;
        if (Has("isRemovedVoidParameter")) t.IsRemovedVoidParameter = true;
        if (Has("isSplittedVarDeclComma")) t.IsSplittedVarDeclComma = true;
        if (Has("isSplittedVarDeclEq")) t.IsSplittedVarDeclEq = true;
        if (Has("isImplicitInt")) t.IsImplicitInt = true;
        if (Has("isComplex")) t.IsComplex = true;
        if (Has("isRestrict")) t.IsRestrict = true;
        if (Has("isAttributeExport")) t.IsAttributeExport = true;
        if (Has("isAnonymous")) t.IsAnonymous = true;
        t.LinkId = r.GetAttribute("link");
        if (Has("varId")) t.VarId = int.Parse(r.GetAttribute("varId")!);
        if (Has("exprId")) t.ExprId = int.Parse(r.GetAttribute("exprId")!);
        t.VariableId = r.GetAttribute("variable");
        t.FunctionId = r.GetAttribute("function");
        t.ValuesId = r.GetAttribute("values");
        t.TypeScopeId = r.GetAttribute("type-scope");
        t.AstParentId = r.GetAttribute("astParent");
        t.AstOperand1Id = r.GetAttribute("astOperand1");
        t.AstOperand2Id = r.GetAttribute("astOperand2");
        t.OriginalName = r.GetAttribute("originalName");
        if (Has("valueType-type"))
        {
            string? bits = r.GetAttribute("valueType-bits");
            t.ValueType = new ValueType
            {
                Type = r.GetAttribute("valueType-type"),
                Sign = r.GetAttribute("valueType-sign"),
                Bits = string.IsNullOrEmpty(bits) ? null : int.Parse(bits),
                Pointer = int.Parse(r.GetAttribute("valueType-pointer") ?? "0"),
                Constness = int.Parse(r.GetAttribute("valueType-constness") ?? "0"),
                Reference = r.GetAttribute("valueType-reference"),
                TypeScopeId = r.GetAttribute("valueType-typeScope"),
                OriginalTypeName = r.GetAttribute("valueType-originalTypeName"),
            };
        }
        t.LoadLocation(r);
        return t;
    }

    internal void SetId(IdMap map)
    {
        Scope = map.Get<Scope>(ScopeId);
        Link = map.Get<Token>(LinkId);
        Variable = map.Get<Variable>(VariableId);
        Function = map.Get<Function>(FunctionId);
        Values = new List<Value>();
        ImpossibleValues = new List<Value>();
        var values = map.Get<List<Value>>(ValuesId);
        if (values != null && values.Count > 0)
        {
            foreach (var v in values)
            {
                if (v.IsImpossible()) ImpossibleValues.Add(v);
                else Values.Add(v);
                v.SetId(map);
            }
        }
        TypeScope = map.Get<Scope>(TypeScopeId);
        AstParent = map.Get<Token>(AstParentId);
        AstOperand1 = map.Get<Token>(AstOperand1Id);
        AstOperand2 = map.Get<Token>(AstOperand2Id);
        if (ValueType != null)
            ValueType.TypeScope = map.Get<Scope>(ValueType.TypeScopeId);
    }

    public Value? GetValue(long v)
    {
        if (Values == null || Values.Count == 0) return null;
        foreach (var value in Values)
            if (value.IntValue == v) return value;
        return null;
    }

    public long? GetKnownIntValue()
    {
        if (Values == null || Values.Count == 0) return null;
        foreach (var value in Values)
            if (value.ValueKind == "known") return value.IntValue;
        return null;
    }

    public bool IsUnaryOp(string op) => AstOperand1 != null && AstOperand2 == null && Str == op;
    public bool IsBinaryOp() => AstOperand1 != null && AstOperand2 != null;

    public IEnumerable<Token> Forward(Token? end = null)
    {
        for (Token? t = this; t != null && t != end; t = t.Next) yield return t;
    }

    public IEnumerable<Token> Backward(Token? start = null)
    {
        for (Token? t = this; t != null && t != start; t = t.Previous) yield return t;
    }

    public IEnumerable<Token> AstParents()
    {
        Token? t = this;
        while (t != null && t.AstParent != null)
        {
            t = t.AstParent;
            yield return t;
        }
    }

    public Token? AstTop()
    {
        Token? top = null;
        foreach (var p in AstParents()) top = p;
        return top;
    }

    public Token? TokAt(int n)
    {
        var tl = n < 0 ? Backward() : Forward();
        n = Math.Abs(n);
        int i = 0;
        foreach (var t in tl)
        {
            if (i == n) return t;
            i++;
        }
        return null;
    }

    public Token? LinkAt(int n) => TokAt(n)?.Link;

    public override string ToString() => $"Token({Str} @{File}:{Linenr}:{Column})";
}

public sealed class Scope
{
    public string? Id { get; set; }
    public string? ClassName { get; set; }
    public string? FunctionId { get; set; }
    public Function? Function { get; set; }
    public List<Function> Functions { get; } = new();
    public string? BodyStartId { get; set; }
    public Token? BodyStart { get; set; }
    public string? BodyEndId { get; set; }
    public Token? BodyEnd { get; set; }
    public string? NestedInId { get; set; }
    public Scope? NestedIn { get; set; }
    public List<Scope> NestedList { get; } = new();
    public string? Type { get; set; }
    public string? DefinedType { get; set; }
    public bool IsExecutable { get; set; }
    public List<string?> VarlistId { get; } = new();
    public List<Variable> Varlist { get; } = new();

    internal void SetId(IdMap map)
    {
        BodyStart = map.Get<Token>(BodyStartId);
        BodyEnd = map.Get<Token>(BodyEndId);
        NestedIn = map.Get<Scope>(NestedInId);
        NestedIn?.NestedList.Add(this);
        Function = map.Get<Function>(FunctionId);
        foreach (var v in VarlistId)
            if (map.TryGet(v) is Variable value) Varlist.Add(value);
    }
}

public sealed class Function
{
    public string? Id { get; set; }
    public string? TokenId { get; set; }
    public Token? Token { get; set; }
    public string? TokenDefId { get; set; }
    public Token? TokenDef { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public bool HasVirtualSpecifier { get; set; }
    public bool IsImplicitlyVirtual { get; set; }
    public string? Access { get; set; }
    public bool IsInlineKeyword { get; set; }
    public bool IsStatic { get; set; }
    public bool IsAttributeNoreturn { get; set; }
    public bool OverriddenFunction { get; set; }
    public Scope? NestedIn { get; set; }
    /// <summary>인자 번호(1부터) → 변수. 원본 dict와 같이 삽입 순서를 유지한다.</summary>
    public SortedDictionary<int, Variable?> Argument { get; } = new();
    public List<KeyValuePair<int, string?>> ArgumentId { get; } = new();

    internal void SetId(IdMap map)
    {
        foreach (var (nr, id) in ArgumentId)
            Argument[nr] = map.Get<Variable>(id);
        Token = map.TryGet(TokenId) as Token;
        TokenDef = map.Get<Token>(TokenDefId);
    }
}

public sealed class Variable
{
    public string? Id { get; set; }
    public string? NameTokenId { get; set; }
    public Token? NameToken { get; set; }
    public string? TypeStartTokenId { get; set; }
    public Token? TypeStartToken { get; set; }
    public string? TypeEndTokenId { get; set; }
    public Token? TypeEndToken { get; set; }
    public string? Access { get; set; }
    public string? ScopeId { get; set; }
    public Scope? Scope { get; set; }
    public bool IsArgument => Access == "Argument";
    public bool IsGlobal => Access == "Global";
    public bool IsLocal => Access == "Local";
    public bool IsArray { get; set; }
    public bool IsClass { get; set; }
    public bool IsConst { get; set; }
    public bool IsExtern { get; set; }
    public bool IsPointer { get; set; }
    public bool IsReference { get; set; }
    public bool IsStatic { get; set; }
    public bool IsVolatile { get; set; }
    public int Constness { get; set; }

    internal void SetId(IdMap map)
    {
        NameToken = map.Get<Token>(NameTokenId);
        TypeStartToken = map.Get<Token>(TypeStartTokenId);
        TypeEndToken = map.Get<Token>(TypeEndTokenId);
        Scope = map.Get<Scope>(ScopeId);
    }
}

public sealed class TypedefInfo : DumpLocation
{
    public string? Name { get; set; }
    public bool Used { get; set; }
}

public sealed class Value
{
    public long? IntValue { get; set; }
    public string? TokValueId { get; set; }
    public Token? TokValue { get; set; }
    public string? FloatValue { get; set; }
    public string? MovedValue { get; set; }
    public string? Uninit { get; set; }
    public string? BufferSize { get; set; }
    public string? ContainerSize { get; set; }
    public string? LifetimeScope { get; set; }
    public string? LifetimeKind { get; set; }
    public string? Bound { get; set; }
    public int? Condition { get; set; }
    public string? ValueKind { get; set; }
    public string? Path { get; set; }

    public bool IsKnown() => ValueKind == "known";
    public bool IsPossible() => ValueKind == "possible";
    public bool IsImpossible() => ValueKind == "impossible";
    public bool IsInconclusive() => ValueKind == "inconclusive";

    internal static Value FromXml(XmlReader r)
    {
        var v = new Value();
        string? iv = r.GetAttribute("intvalue");
        if (!string.IsNullOrEmpty(iv))
            v.IntValue = long.TryParse(iv, out long parsed) ? parsed : (iv.StartsWith('-') ? long.MinValue : long.MaxValue);
        v.TokValueId = r.GetAttribute("tokvalue");
        v.FloatValue = r.GetAttribute("floatvalue");
        v.MovedValue = r.GetAttribute("movedvalue");
        v.Uninit = r.GetAttribute("uninit");
        v.BufferSize = r.GetAttribute("buffer-size");
        v.ContainerSize = r.GetAttribute("container-size");
        v.LifetimeScope = r.GetAttribute("lifetime-scope");
        v.LifetimeKind = r.GetAttribute("lifetime-kind");
        v.Bound = r.GetAttribute("bound");
        string? cond = r.GetAttribute("condition-line");
        if (!string.IsNullOrEmpty(cond)) v.Condition = int.Parse(cond);
        if (!string.IsNullOrEmpty(r.GetAttribute("known"))) v.ValueKind = "known";
        else if (!string.IsNullOrEmpty(r.GetAttribute("possible"))) v.ValueKind = "possible";
        else if (!string.IsNullOrEmpty(r.GetAttribute("impossible"))) v.ValueKind = "impossible";
        else if (!string.IsNullOrEmpty(r.GetAttribute("inconclusive"))) v.ValueKind = "inconclusive";
        v.Path = r.GetAttribute("path");
        return v;
    }

    internal void SetId(IdMap map) => TokValue = map.TryGet(TokValueId) as Token;
}

public sealed class Standards
{
    public string? C { get; set; } = "";
    public string? Cpp { get; set; } = "";
    public bool Posix { get; set; }
}

public sealed class Platform
{
    public string? Name { get; set; }
    public int CharBit { get; set; }
    public int ShortBit { get; set; }
    public int IntBit { get; set; }
    public int LongBit { get; set; }
    public int LongLongBit { get; set; }
    public int PointerBit { get; set; }
}

public sealed class Configuration
{
    public string Name { get; set; } = "";
    public List<Directive> Directives { get; } = new();
    public List<MacroUsage> MacroUsage { get; } = new();
    public List<PreprocessorIfCondition> PreprocessorIfConditions { get; } = new();
    public List<Token> Tokenlist { get; } = new();
    public List<Scope> Scopes { get; } = new();
    public List<Function> Functions { get; } = new();
    public List<Variable> Variables { get; } = new();
    public List<TypedefInfo> TypedefInfo { get; } = new();
    public List<(string? Id, List<Value> Values)> Valueflow { get; } = new();
    public Standards Standards { get; } = new();
    /// <summary>clang 가져오기 모드의 경고 (file, line, column, message)</summary>
    public List<(DumpLocation Location, string Message)> ClangWarnings { get; } = new();
    internal List<Variable> Arguments { get; } = new();

    internal void SetIdMap()
    {
        Token? prev = null;
        foreach (var token in Tokenlist)
        {
            token.Previous = prev;
            if (prev != null) prev.Next = token;
            prev = token;
        }

        var map = new IdMap();
        foreach (var t in Tokenlist) map.Set(t.Id, t);
        foreach (var s in Scopes) map.Set(s.Id, s);
        foreach (var f in Functions) map.Set(f.Id, f);
        foreach (var v in Variables) map.Set(v.Id, v);
        foreach (var v in Arguments) map.Set(v.Id, v);
        foreach (var (id, values) in Valueflow) map.Set(id, values);
        foreach (var t in Tokenlist) t.SetId(map);
        foreach (var s in Scopes) s.SetId(map);
        foreach (var f in Functions) f.SetId(map);
        foreach (var v in Variables) v.SetId(map);
        foreach (var v in Arguments) v.SetId(map);
    }
}

/// <summary>원본 IdMap: None, '0', '00000000' 등은 None으로 매핑된다. 없는 ID는 원본처럼 null로 처리한다.</summary>
internal sealed class IdMap
{
    private readonly Dictionary<string, object> _map = new(StringComparer.Ordinal);
    private static bool IsNullId(string? id) =>
        id is null or "0" or "00000000" or "0000000000000000" or "0x0";

    public void Set(string? id, object value)
    {
        if (!IsNullId(id)) _map[id!] = value;
    }

    public object? TryGet(string? id) => IsNullId(id) ? null : _map.GetValueOrDefault(id!);

    public T? Get<T>(string? id) where T : class => TryGet(id) as T;
}

public sealed class CppcheckData
{
    public string? Language { get; private set; }
    public string Filename { get; }
    public List<Token> RawTokens { get; } = new();
    public Platform? Platform { get; private set; }
    public List<string> Files { get; } = new();

    private CppcheckData(string filename) => Filename = filename;

    private static XmlReader Open(string filename) =>
        XmlReader.Create(filename, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true });

    public static CppcheckData Parse(string filename)
    {
        var data = new CppcheckData(filename);
        using (var r = Open(filename))
        {
            bool inRawTokens = false;
            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.EndElement)
                {
                    if (r.Name == "rawtokens") break;
                    continue;
                }
                if (r.NodeType != XmlNodeType.Element) continue;
                switch (r.Name)
                {
                    case "dumps":
                        data.Language = r.GetAttribute("language");
                        break;
                    case "platform" when data.Platform == null:
                        data.Platform = new Platform
                        {
                            Name = r.GetAttribute("name"),
                            CharBit = int.Parse(r.GetAttribute("char_bit")!),
                            ShortBit = int.Parse(r.GetAttribute("short_bit")!),
                            IntBit = int.Parse(r.GetAttribute("int_bit")!),
                            LongBit = int.Parse(r.GetAttribute("long_bit")!),
                            LongLongBit = int.Parse(r.GetAttribute("long_long_bit")!),
                            PointerBit = int.Parse(r.GetAttribute("pointer_bit")!),
                        };
                        break;
                    case "rawtokens":
                        inRawTokens = !r.IsEmptyElement;
                        if (r.IsEmptyElement) goto done;
                        break;
                    case "file" when inRawTokens:
                        data.Files.Add(r.GetAttribute("name") ?? "");
                        break;
                    case "tok" when inRawTokens:
                        var tok = Token.FromXml(r);
                        tok.File = data.Files[int.Parse(r.GetAttribute("fileIndex")!)];
                        data.RawTokens.Add(tok);
                        break;
                    // Cppcheck 2.22는 <rawtokens>를 <dump> 구성들 뒤에 기록하므로 끝까지 읽는다.
                }
            }
        done:;
        }
        for (int i = 0; i < data.RawTokens.Count - 1; i++)
        {
            data.RawTokens[i + 1].Previous = data.RawTokens[i];
            data.RawTokens[i].Next = data.RawTokens[i + 1];
        }
        return data;
    }

    /// <summary>dump 파일의 구성(configuration)을 문서 순서대로 하나씩 읽는다.</summary>
    public IEnumerable<Configuration> IterConfigurations()
    {
        using var r = Open(Filename);
        Configuration? cfg = null;
        Function? cfgFunction = null;
        (string? Id, List<Value> Values)? cfgValueflow = null;
        bool iterScopeVarlist = false, iterTypedefInfo = false, iterDirective = false;

        while (r.Read())
        {
            bool isStart = r.NodeType == XmlNodeType.Element;
            bool isEnd = r.NodeType == XmlNodeType.EndElement || (isStart && r.IsEmptyElement);
            if (!isStart && !isEnd) continue;
            string tag = r.Name;

            switch (tag)
            {
                case "dump":
                    if (isStart) cfg = new Configuration { Name = r.GetAttribute("cfg") ?? "" };
                    if (isEnd && cfg != null)
                    {
                        cfg.SetIdMap();
                        yield return cfg;
                        cfg = null;
                    }
                    break;
                case "clang-warning" when isStart && cfg != null:
                    var cw = new DumpLocation
                    {
                        File = r.GetAttribute("file"),
                        Linenr = int.Parse(r.GetAttribute("line")!),
                        Column = int.Parse(r.GetAttribute("column")!),
                    };
                    cfg.ClangWarnings.Add((cw, r.GetAttribute("message") ?? ""));
                    break;
                case "c" when isStart && cfg != null:
                    cfg.Standards.C = r.GetAttribute("version");
                    break;
                case "cpp" when isStart && cfg != null:
                    cfg.Standards.Cpp = r.GetAttribute("version");
                    break;
                case "posix" when isStart && cfg != null:
                    cfg.Standards.Posix = r.GetAttribute("posix") != null;
                    break;
                case "directive":
                    if (isStart)
                    {
                        var d = new Directive { Str = r.GetAttribute("str") ?? "" };
                        d.LoadLocation(r);
                        cfg!.Directives.Add(d);
                        iterDirective = true;
                    }
                    if (isEnd) iterDirective = false;
                    break;
                case "macro" when isStart:
                    var m = new MacroUsage
                    {
                        Name = r.GetAttribute("name"),
                        UseFile = r.GetAttribute("usefile"),
                        UseLine = r.GetAttribute("useline"),
                        UseColumn = r.GetAttribute("usecolumn"),
                        IsKnownValue = (r.GetAttribute("is-known-value") ?? "false") == "true",
                    };
                    m.LoadLocation(r);
                    cfg!.MacroUsage.Add(m);
                    break;
                case "if-cond" when isStart:
                    var ic = new PreprocessorIfCondition { E = r.GetAttribute("E"), Result = int.Parse(r.GetAttribute("result")!) };
                    ic.LoadLocation(r);
                    cfg!.PreprocessorIfConditions.Add(ic);
                    break;
                case "token" when isStart && !iterDirective && !iterTypedefInfo:
                    cfg!.Tokenlist.Add(Token.FromXml(r));
                    break;
                case "scope" when isStart:
                    string? type = r.GetAttribute("type");
                    cfg!.Scopes.Add(new Scope
                    {
                        Id = r.GetAttribute("id"),
                        ClassName = r.GetAttribute("className"),
                        FunctionId = r.GetAttribute("function"),
                        BodyStartId = r.GetAttribute("bodyStart"),
                        BodyEndId = r.GetAttribute("bodyEnd"),
                        NestedInId = r.GetAttribute("nestedIn"),
                        Type = type,
                        DefinedType = r.GetAttribute("definedType"),
                        IsExecutable = type is "Function" or "If" or "Else" or "For" or "While" or "Do" or
                            "Switch" or "Try" or "Catch" or "Unconditional" or "Lambda",
                    });
                    break;
                case "varlist":
                    if (isStart) iterScopeVarlist = true;
                    if (isEnd) iterScopeVarlist = false;
                    break;
                case "function":
                    if (isStart)
                    {
                        cfgFunction = new Function
                        {
                            Id = r.GetAttribute("id"),
                            TokenId = r.GetAttribute("token"),
                            TokenDefId = r.GetAttribute("tokenDef"),
                            Name = r.GetAttribute("name"),
                            Type = r.GetAttribute("type"),
                            HasVirtualSpecifier = (r.GetAttribute("hasVirtualSpecifier") ?? "false") == "true",
                            IsImplicitlyVirtual = (r.GetAttribute("isImplicitlyVirtual") ?? "false") == "true",
                            Access = r.GetAttribute("access"),
                            IsInlineKeyword = (r.GetAttribute("isInlineKeyword") ?? "false") == "true",
                            IsStatic = (r.GetAttribute("isStatic") ?? "false") == "true",
                            IsAttributeNoreturn = (r.GetAttribute("isAttributeNoreturn") ?? "false") == "true",
                            OverriddenFunction = (r.GetAttribute("overriddenFunction") ?? "false") == "true",
                            NestedIn = cfg!.Scopes.Count > 0 ? cfg.Scopes[^1] : null,
                        };
                    }
                    if (isEnd && cfgFunction != null)
                    {
                        cfg!.Functions.Add(cfgFunction);
                        cfgFunction = null;
                    }
                    break;
                case "arg" when isStart && cfgFunction != null:
                    cfgFunction.ArgumentId.Add(new(int.Parse(r.GetAttribute("nr")!), r.GetAttribute("variable")));
                    break;
                case "var" when isStart:
                    if (iterScopeVarlist)
                    {
                        cfg!.Scopes[^1].VarlistId.Add(r.GetAttribute("id"));
                    }
                    else
                    {
                        var v = new Variable
                        {
                            Id = r.GetAttribute("id"),
                            NameTokenId = r.GetAttribute("nameToken"),
                            TypeStartTokenId = r.GetAttribute("typeStartToken"),
                            TypeEndTokenId = r.GetAttribute("typeEndToken"),
                            Access = r.GetAttribute("access"),
                            ScopeId = r.GetAttribute("scope"),
                            Constness = int.Parse(r.GetAttribute("constness") ?? "0"),
                            IsArray = r.GetAttribute("isArray") == "true",
                            IsClass = r.GetAttribute("isClass") == "true",
                            IsConst = r.GetAttribute("isConst") == "true",
                            IsExtern = r.GetAttribute("isExtern") == "true",
                            IsPointer = r.GetAttribute("isPointer") == "true",
                            IsReference = r.GetAttribute("isReference") == "true",
                            IsStatic = r.GetAttribute("isStatic") == "true",
                            IsVolatile = r.GetAttribute("isVolatile") == "true",
                        };
                        if (!string.IsNullOrEmpty(v.NameTokenId)) cfg!.Variables.Add(v);
                        else cfg!.Arguments.Add(v);
                    }
                    break;
                case "typedef-info":
                    iterTypedefInfo = isStart && !r.IsEmptyElement;
                    break;
                case "info" when iterTypedefInfo && isStart:
                    var ti = new TypedefInfo { Name = r.GetAttribute("name"), Used = r.GetAttribute("used") == "1" };
                    ti.LoadLocation(r);
                    cfg!.TypedefInfo.Add(ti);
                    break;
                case "values":
                    if (isStart) cfgValueflow = (r.GetAttribute("id"), new List<Value>());
                    if (isEnd && cfgValueflow != null)
                    {
                        cfg!.Valueflow.Add(cfgValueflow.Value);
                        cfgValueflow = null;
                    }
                    break;
                case "value" when isStart && cfgValueflow != null:
                    cfgValueflow.Value.Values.Add(Value.FromXml(r));
                    break;
            }
        }
    }
}

/// <summary>cppcheckdata.py의 모듈 함수</summary>
public static class CppcheckDataFunctions
{
    public static bool SimpleMatch(Token? token, string pattern)
    {
        foreach (var p in pattern.Split(' '))
        {
            if (token == null || token.Str != p) return false;
            token = token.Next;
        }
        return true;
    }

    private static void GetArgumentsRecursive(Token? tok, List<Token> arguments)
    {
        if (tok == null) return;
        if (tok.Str == ",")
        {
            GetArgumentsRecursive(tok.AstOperand1, arguments);
            GetArgumentsRecursive(tok.AstOperand2, arguments);
        }
        else
        {
            arguments.Add(tok);
        }
    }

    public static List<Token>? GetArguments(Token ftok)
    {
        if (!ftok.IsName || ftok.Next == null || ftok.Next.Str != "(") return null;
        var args = new List<Token>();
        GetArgumentsRecursive(ftok.Next.AstOperand2, args);
        return args;
    }

    /// <summary>get_function_call_name_args: 함수 호출 이름과 인자 목록. 호출이 아니면 (null, null).</summary>
    public static (string? Name, List<Token>? Args) GetFunctionCallNameArgs(Token? token)
    {
        if (token == null) return (null, null);
        if (!token.IsName || token.Scope == null || !token.Scope.IsExecutable) return (null, null);
        if (!SimpleMatch(token.Next, "(")) return (null, null);
        Token nametok;
        string name;
        if (token.Function != null)
        {
            nametok = token.Function.Token ?? token.Function.TokenDef!;
            if (token == token.Function.Token || token == token.Function.TokenDef) return (null, null);
            name = nametok.Str;
            while (nametok.Previous?.Previous != null && nametok.Previous.Str == "::" && nametok.Previous.Previous.IsName)
            {
                name = nametok.Previous.Previous.Str + "::" + name;
                nametok = nametok.Previous.Previous;
            }
            for (var scope = token.Function.NestedIn; scope != null; scope = scope.NestedIn)
                if (!string.IsNullOrEmpty(scope.ClassName)) name = scope.ClassName + "::" + name;
        }
        else
        {
            nametok = token;
            name = nametok.Str;
            while (nametok.Previous?.Previous != null && nametok.Previous.Str == "::" && nametok.Previous.Previous.IsName)
            {
                name = nametok.Previous.Previous.Str + "::" + name;
                nametok = nametok.Previous.Previous;
            }
        }
        return (name, GetArguments(token));
    }
}
