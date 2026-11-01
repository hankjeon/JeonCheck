using System.Xml.Linq;
using JeonCheck.Models;

namespace JeonCheck.Services;

public class DumpFileParser
{
    public DumpDocument Parse(string dumpFilePath)
    {
        var doc = new DumpDocument();
        var xdoc = XDocument.Load(dumpFilePath);
        var root = xdoc.Root;
        if (root == null)
            return doc;

        // 1. 플랫폼 비트 폭 파싱
        var platElem = root.Element("platform");
        if (platElem != null)
        {
            doc.Platform.CharBit = ParseInt(platElem.Attribute("char_bit"), 8);
            doc.Platform.ShortBit = ParseInt(platElem.Attribute("short_bit"), 16);
            doc.Platform.IntBit = ParseInt(platElem.Attribute("int_bit"), 32);
            doc.Platform.LongBit = ParseInt(platElem.Attribute("long_bit"), 32);
            doc.Platform.LongLongBit = ParseInt(platElem.Attribute("long_long_bit"), 64);
            doc.Platform.FloatBit = ParseInt(platElem.Attribute("float_bit"), 32);
            doc.Platform.DoubleBit = ParseInt(platElem.Attribute("double_bit"), 64);
            doc.Platform.LongDoubleBit = ParseInt(platElem.Attribute("long_double_bit"), 64);
            doc.Platform.PointerBit = ParseInt(platElem.Attribute("pointer_bit"), 32);
        }

        // 2. 각 <dump> 설정 단위 파싱
        foreach (var dumpElem in root.Elements("dump"))
        {
            var unit = ParseDumpUnit(dumpElem);
            doc.Configurations.Add(unit);
        }

        // 3. <rawtokens> 및 <tokenlist> 비교를 통한 라인별 검사 완료 / 미검사(Skipped) 판별
        BuildLineCoverage(root, doc);

        return doc;
    }

    private static DumpCfgUnit ParseDumpUnit(XElement dumpElem)
    {
        var unit = new DumpCfgUnit
        {
            CfgName = dumpElem.Attribute("cfg")?.Value ?? string.Empty
        };

        // Directivelist
        var dirListElem = dumpElem.Element("directivelist");
        if (dirListElem != null)
        {
            foreach (var dElem in dirListElem.Elements("directive"))
            {
                unit.Directives.Add(new DumpDirective
                {
                    File = dElem.Attribute("file")?.Value ?? string.Empty,
                    LineNr = ParseInt(dElem.Attribute("linenr"), 0),
                    Str = dElem.Attribute("str")?.Value ?? string.Empty
                });
            }
        }

        // Tokenlist
        var tokenListElem = dumpElem.Element("tokenlist");
        if (tokenListElem != null)
        {
            DumpToken? prev = null;
            foreach (var tElem in tokenListElem.Elements("token"))
            {
                var tok = new DumpToken
                {
                    Id = tElem.Attribute("id")?.Value ?? string.Empty,
                    File = tElem.Attribute("file")?.Value ?? string.Empty,
                    LineNr = ParseInt(tElem.Attribute("linenr"), 0),
                    Column = ParseInt(tElem.Attribute("column"), 0),
                    Str = tElem.Attribute("str")?.Value ?? string.Empty,
                    ScopeId = tElem.Attribute("scope")?.Value ?? string.Empty,
                    Type = tElem.Attribute("type")?.Value ?? string.Empty,
                    LinkId = tElem.Attribute("link")?.Value ?? string.Empty,
                    VariableId = tElem.Attribute("variable")?.Value ?? string.Empty,
                    FunctionId = tElem.Attribute("function")?.Value ?? string.Empty,
                    AstParentId = tElem.Attribute("astParent")?.Value ?? string.Empty,
                    AstOperand1Id = tElem.Attribute("astOperand1")?.Value ?? string.Empty,
                    AstOperand2Id = tElem.Attribute("astOperand2")?.Value ?? string.Empty,
                    IsAssignmentOp = ParseBool(tElem.Attribute("isAssignmentOp")),
                    IsComparisonOp = ParseBool(tElem.Attribute("isComparisonOp")),
                    IsArithmeticalOp = ParseBool(tElem.Attribute("isArithmeticalOp")),
                    IsCast = ParseBool(tElem.Attribute("isCast")),
                    IsInsertedBrace = ParseBool(tElem.Attribute("isInsertedBrace")),
                    IsSplittedVarDeclEq = ParseBool(tElem.Attribute("isSplittedVarDeclEq")),
                    IsIntLiteral = ParseBool(tElem.Attribute("isInt")),
                    IsFloatLiteral = ParseBool(tElem.Attribute("isFloat")),
                    IsSigned = ParseBool(tElem.Attribute("isSigned")),
                    IsUnsigned = ParseBool(tElem.Attribute("isUnsigned")),
                    IsRemovedVoidParameter = ParseBool(tElem.Attribute("isRemovedVoidParameter")),
                    VarId = tElem.Attribute("varId")?.Value ?? string.Empty,
                    MacroName = tElem.Attribute("macroName")?.Value ?? string.Empty,
                    ValueType = tElem.Attribute("valueType-type")?.Value,
                    ValueSign = tElem.Attribute("valueType-sign")?.Value,
                    ValuePointer = ParseInt(tElem.Attribute("valueType-pointer"), 0),
                    ValueConstness = ParseInt(tElem.Attribute("valueType-constness"), 0),
                    ValueVolatileness = ParseInt(tElem.Attribute("valueType-volatileness"), 0),
                    ValueTypeScopeId = tElem.Attribute("valueType-typeScope")?.Value ?? string.Empty,
                    OriginalTypeName = tElem.Attribute("valueType-originalTypeName")?.Value
                };

                tok.Previous = prev;
                if (prev != null)
                    prev.Next = tok;
                prev = tok;

                unit.Tokens.Add(tok);
                if (!string.IsNullOrEmpty(tok.Id))
                    unit.TokenById[tok.Id] = tok;
            }
        }

        // Scopes
        var scopesElem = dumpElem.Element("scopes");
        if (scopesElem != null)
        {
            foreach (var sElem in scopesElem.Elements("scope"))
            {
                var scope = new DumpScope
                {
                    Id = sElem.Attribute("id")?.Value ?? string.Empty,
                    Type = sElem.Attribute("type")?.Value ?? string.Empty,
                    ClassName = sElem.Attribute("className")?.Value ?? string.Empty,
                    BodyStartId = sElem.Attribute("bodyStart")?.Value ?? string.Empty,
                    BodyEndId = sElem.Attribute("bodyEnd")?.Value ?? string.Empty,
                    NestedInId = sElem.Attribute("nestedIn")?.Value ?? string.Empty,
                    FunctionId = sElem.Attribute("function")?.Value ?? string.Empty
                };
                if (!string.IsNullOrEmpty(scope.Id))
                    unit.ScopeById[scope.Id] = scope;

                // 함수 목록 (선언/정의 토큰 및 매개변수 변수 ID)
                var funcListElem = sElem.Element("functionList");
                if (funcListElem != null)
                {
                    foreach (var fElem in funcListElem.Elements("function"))
                    {
                        var func = new DumpFunction
                        {
                            Id = fElem.Attribute("id")?.Value ?? string.Empty,
                            Name = fElem.Attribute("name")?.Value ?? string.Empty,
                            TokenId = fElem.Attribute("token")?.Value ?? string.Empty,
                            TokenDefId = fElem.Attribute("tokenDef")?.Value ?? string.Empty,
                            ArgVariableIds = fElem.Elements("arg")
                                .OrderBy(a => ParseInt(a.Attribute("nr"), 0))
                                .Select(a => a.Attribute("variable")?.Value ?? string.Empty)
                                .ToList()
                        };
                        if (!string.IsNullOrEmpty(func.Id))
                            unit.FunctionById[func.Id] = func;
                    }
                }
            }
        }

        // Variables
        var varsElem = dumpElem.Element("variables");
        if (varsElem != null)
        {
            foreach (var vElem in varsElem.Elements("var"))
            {
                var v = new DumpVariable
                {
                    Id = vElem.Attribute("id")?.Value ?? string.Empty,
                    NameTokenId = vElem.Attribute("nameToken")?.Value ?? string.Empty,
                    TypeStartTokenId = vElem.Attribute("typeStartToken")?.Value ?? string.Empty,
                    TypeEndTokenId = vElem.Attribute("typeEndToken")?.Value ?? string.Empty,
                    Access = vElem.Attribute("access")?.Value ?? string.Empty,
                    ScopeId = vElem.Attribute("scope")?.Value ?? string.Empty,
                    IsPointer = ParseBool(vElem.Attribute("isPointer")),
                    IsArray = ParseBool(vElem.Attribute("isArray")),
                    IsConst = ParseBool(vElem.Attribute("isConst")),
                    IsVolatile = ParseBool(vElem.Attribute("isVolatile")),
                    IsStatic = ParseBool(vElem.Attribute("isStatic")),
                    Constness = ParseInt(vElem.Attribute("constness"), 0),
                    Volatileness = ParseInt(vElem.Attribute("volatileness"), 0)
                };
                if (!string.IsNullOrEmpty(v.NameTokenId) && unit.TokenById.TryGetValue(v.NameTokenId, out var nameTok))
                    v.NameToken = nameTok;
                if (!string.IsNullOrEmpty(v.TypeStartTokenId) && unit.TokenById.TryGetValue(v.TypeStartTokenId, out var typeStartTok))
                    v.TypeStartToken = typeStartTok;
                if (!string.IsNullOrEmpty(v.Id))
                    unit.VariableById[v.Id] = v;
            }
        }

        // 함수의 선언/정의 토큰 및 매개변수 변수 연결
        foreach (var func in unit.FunctionById.Values)
        {
            if (unit.TokenById.TryGetValue(func.TokenId, out var implTok))
                func.Token = implTok;
            if (unit.TokenById.TryGetValue(func.TokenDefId, out var defTok))
                func.TokenDef = defTok;
            func.Args = func.ArgVariableIds
                .Select(id => unit.VariableById.TryGetValue(id, out var argVar) ? argVar : null)
                .ToList();
        }

        // AST 및 참조 링크 연결
        foreach (var tok in unit.Tokens)
        {
            if (!string.IsNullOrEmpty(tok.LinkId) && unit.TokenById.TryGetValue(tok.LinkId, out var linkTok))
                tok.Link = linkTok;
            if (!string.IsNullOrEmpty(tok.AstParentId) && unit.TokenById.TryGetValue(tok.AstParentId, out var parentTok))
                tok.AstParent = parentTok;
            if (!string.IsNullOrEmpty(tok.AstOperand1Id) && unit.TokenById.TryGetValue(tok.AstOperand1Id, out var op1Tok))
                tok.AstOperand1 = op1Tok;
            if (!string.IsNullOrEmpty(tok.AstOperand2Id) && unit.TokenById.TryGetValue(tok.AstOperand2Id, out var op2Tok))
                tok.AstOperand2 = op2Tok;
            if (!string.IsNullOrEmpty(tok.VariableId) && unit.VariableById.TryGetValue(tok.VariableId, out var variable))
                tok.Variable = variable;
        }

        return unit;
    }

    private static void BuildLineCoverage(XElement root, DumpDocument doc)
    {
        // 1. 각 <dump> 내의 directive 라인과 실제 분석된 tokenlist 라인을 파일별로 수집
        foreach (var unit in doc.Configurations)
        {
            foreach (var dir in unit.Directives)
            {
                if (string.IsNullOrEmpty(dir.File) || dir.LineNr <= 0)
                    continue;
                var cov = GetOrCreateCoverage(doc, dir.File);
                cov.DirectiveLines.Add(dir.LineNr);
            }

            foreach (var tok in unit.Tokens)
            {
                if (string.IsNullOrEmpty(tok.File) || tok.LineNr <= 0)
                    continue;
                var cov = GetOrCreateCoverage(doc, tok.File);
                cov.AnalyzedLines.Add(tok.LineNr);
            }
        }

        // 2. <rawtokens>에서 주석/전처리기가 아닌 실제 C 코드 토큰이 있는 줄 중 AnalyzedLines에 없는 줄을 SkippedLines로 분류
        var rawTokensElem = root.Element("rawtokens");
        if (rawTokensElem == null)
            return;

        var fileByIndex = new Dictionary<string, string>();
        foreach (var fElem in rawTokensElem.Elements("file"))
        {
            string idx = fElem.Attribute("index")?.Value ?? string.Empty;
            string name = fElem.Attribute("name")?.Value ?? string.Empty;
            if (!string.IsNullOrEmpty(idx) && !string.IsNullOrEmpty(name))
                fileByIndex[idx] = name;
        }

        var rawTokensByFileLine = new Dictionary<(string File, int Line), List<string>>();
        foreach (var rTok in rawTokensElem.Elements("tok"))
        {
            string fIdx = rTok.Attribute("fileIndex")?.Value ?? string.Empty;
            int lineNr = ParseInt(rTok.Attribute("linenr"), 0);
            string str = rTok.Attribute("str")?.Value ?? string.Empty;

            if (lineNr <= 0 || string.IsNullOrEmpty(str) || !fileByIndex.TryGetValue(fIdx, out string? filePath))
                continue;

            // 주석 토큰 제외
            if (str.StartsWith("/*", StringComparison.Ordinal) || str.StartsWith("//", StringComparison.Ordinal))
                continue;

            var key = (filePath, lineNr);
            if (!rawTokensByFileLine.TryGetValue(key, out var list))
            {
                list = new List<string>();
                rawTokensByFileLine[key] = list;
            }
            list.Add(str);
        }

        foreach (var kvp in rawTokensByFileLine)
        {
            string filePath = kvp.Key.File;
            int lineNr = kvp.Key.Line;
            var tokensOnLine = kvp.Value;

            if (tokensOnLine.Count == 0)
                continue;

            var cov = GetOrCreateCoverage(doc, filePath);

            // 전처리 지시문(#include, #define, #ifdef, #else, #endif 등)으로 시작하는 줄은 Neutral 유지
            if (tokensOnLine[0] == "#" || cov.DirectiveLines.Contains(lineNr))
                continue;

            if (!cov.AnalyzedLines.Contains(lineNr))
            {
                cov.SkippedLines.Add(lineNr);
            }
        }
    }

    private static FileLineCoverage GetOrCreateCoverage(DumpDocument doc, string filePath)
    {
        string normKey = AnalysisIssue.NormalizePath(filePath);
        if (!doc.CoverageByFile.TryGetValue(normKey, out var cov))
        {
            cov = new FileLineCoverage { FilePath = filePath };
            doc.CoverageByFile[normKey] = cov;
        }
        return cov;
    }

    private static int ParseInt(XAttribute? attr, int defaultVal)
    {
        if (attr != null && int.TryParse(attr.Value, out int v))
            return v;
        return defaultVal;
    }

    private static bool ParseBool(XAttribute? attr)
    {
        return attr != null && string.Equals(attr.Value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
