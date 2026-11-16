using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using JeonCheck.Models;

namespace JeonCheck.Services;

/// <summary>
/// 전처리 후 AST(tokenlist)와 전처리 전 원본 토큰·주석(rawtokens)을 결합해
/// 복사/붙여넣기 및 문맥 불일치 오류 6종을 검출하는 자체 C# 정적 분석기.
/// </summary>
public class CopyPasteAnalyzer
{
    public const string RuleMacroCommentMismatch = "copyPasteMacroCommentMismatch";
    public const string RuleSequenceMismatch = "copyPasteSequenceMismatch";
    public const string RuleConsecutiveOverwrite = "consecutiveTargetOverwrite";
    public const string RuleConditionBodyMismatch = "conditionBodyMismatch";
    public const string RuleLoopIndexMismatch = "loopIndexMismatch";
    public const string RuleSuspiciousClone = "suspiciousCodeClone";

    private static readonly Regex StandardTypeRegex = new(
        @"^(?:u?int(?:_fast|_least)?(?:8|16|32|64)_t|float(?:32|64|128)_t)$",
        RegexOptions.Compiled);

    private static readonly Regex DigitGroupRegex = new(@"\d+", RegexOptions.Compiled);

    private static readonly Regex CommentTaggedNumberRegex = new(
        @"(?<tag>[A-Za-z가-힣_]+)\s*[-_#:]?\s*(?<num>\d+)",
        RegexOptions.Compiled);

    private static readonly Regex GenericStepCommentTagRegex = new(
        @"^(?:step|phase|stage|case|test|part|item|todo|fixme|line|ver|rev|v)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> CKeywords = new(StringComparer.Ordinal)
    {
        "auto", "break", "case", "char", "const", "continue", "default", "do", "double",
        "else", "enum", "extern", "float", "for", "goto", "if", "inline", "int", "long",
        "register", "restrict", "return", "short", "signed", "sizeof", "static", "struct",
        "switch", "typedef", "union", "unsigned", "void", "volatile", "while",
        "_Bool", "_Complex", "_Imaginary", "_Alignas", "_Alignof", "_Atomic", "_Generic",
        "_Noreturn", "_Static_assert", "_Thread_local", "bool", "true", "false", "NULL"
    };

    private static readonly HashSet<string> ComparisonOps = new(StringComparer.Ordinal)
    {
        "<", "<=", ">", ">=", "==", "!="
    };

    public List<AnalysisIssue> Analyze(
        DumpDocument dumpDoc,
        HashSet<string> targetFilesNormalized,
        Dictionary<string, RuleInfo> rulesById)
    {
        var issues = new List<AnalysisIssue>();
        var emittedKeys = new HashSet<string>();

        bool checkMacroComment = IsRuleEnabled(rulesById, RuleMacroCommentMismatch);
        bool checkSequence = IsRuleEnabled(rulesById, RuleSequenceMismatch);
        bool checkOverwrite = IsRuleEnabled(rulesById, RuleConsecutiveOverwrite);
        bool checkCondBody = IsRuleEnabled(rulesById, RuleConditionBodyMismatch);
        bool checkLoopIndex = IsRuleEnabled(rulesById, RuleLoopIndexMismatch);
        bool checkClone = IsRuleEnabled(rulesById, RuleSuspiciousClone);

        if (!checkMacroComment && !checkSequence && !checkOverwrite &&
            !checkCondBody && !checkLoopIndex && !checkClone)
        {
            return issues;
        }

        var rawIndex = BuildRawTokenIndex(dumpDoc);

        foreach (var unit in dumpDoc.Configurations)
        {
            var unitsToAnalyze = ExtractFunctionTokenRanges(unit);
            foreach (var (tokens, loopVarsInScope) in unitsToAnalyze)
            {
                var stmts = ParseStatements(tokens);
                var ctx = new AnalysisContext(unit, rawIndex, targetFilesNormalized, rulesById, issues, emittedKeys);

                if (checkOverwrite)
                    AnalyzeConsecutiveOverwrites(stmts, ctx);

                if (checkCondBody)
                    AnalyzeConditionBodyMismatches(stmts, ctx);

                if (checkLoopIndex)
                    AnalyzeLoopIndexMismatches(stmts, loopVarsInScope, ctx);

                // Rule 1과 Rule 2가 동일 위치에서 중복 보고되지 않도록 Rule 1이 보고한 문장 줄 추적
                var macroReportedLines = new HashSet<(string FileNorm, int Line)>();
                if (checkMacroComment)
                    AnalyzeMacroCommentMismatches(stmts, ctx, macroReportedLines);

                if (checkSequence)
                    AnalyzeSequenceMismatches(stmts, ctx, macroReportedLines);

                if (checkClone)
                    AnalyzeSuspiciousClones(stmts, ctx);
            }
        }

        return issues;
    }

    private static bool IsRuleEnabled(Dictionary<string, RuleInfo> rulesById, string ruleId)
        => rulesById.TryGetValue(ruleId, out var rule) && rule.IsEnabled;

    // -------------------------------------------------------------------------
    // 원시 토큰(<rawtokens>) 및 주석 인덱스
    // -------------------------------------------------------------------------

    private sealed class RawFileIndex
    {
        public Dictionary<(int Line, int Col), DumpRawToken> ByPosition { get; } = new();
        public Dictionary<int, List<DumpRawToken>> CodeTokensByLine { get; } = new();
        public List<DumpRawToken> Comments { get; } = new();
    }

    private static Dictionary<string, RawFileIndex> BuildRawTokenIndex(DumpDocument dumpDoc)
    {
        var result = new Dictionary<string, RawFileIndex>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in dumpDoc.RawTokensByFile)
        {
            var idx = new RawFileIndex();
            foreach (var rt in kvp.Value)
            {
                if (rt.IsComment)
                {
                    idx.Comments.Add(rt);
                }
                else
                {
                    if (rt.Column > 0)
                        idx.ByPosition.TryAdd((rt.LineNr, rt.Column), rt);
                    if (!idx.CodeTokensByLine.TryGetValue(rt.LineNr, out var lineList))
                    {
                        lineList = new List<DumpRawToken>();
                        idx.CodeTokensByLine[rt.LineNr] = lineList;
                    }
                    lineList.Add(rt);
                }
            }
            result[kvp.Key] = idx;
        }
        return result;
    }

    private sealed class AnalysisContext
    {
        public DumpCfgUnit Unit { get; }
        public Dictionary<string, RawFileIndex> RawIndex { get; }
        public HashSet<string> TargetFilesNormalized { get; }
        public Dictionary<string, RuleInfo> RulesById { get; }
        public List<AnalysisIssue> Issues { get; }
        public HashSet<string> EmittedKeys { get; }

        public AnalysisContext(
            DumpCfgUnit unit,
            Dictionary<string, RawFileIndex> rawIndex,
            HashSet<string> targetFilesNormalized,
            Dictionary<string, RuleInfo> rulesById,
            List<AnalysisIssue> issues,
            HashSet<string> emittedKeys)
        {
            Unit = unit;
            RawIndex = rawIndex;
            TargetFilesNormalized = targetFilesNormalized;
            RulesById = rulesById;
            Issues = issues;
            EmittedKeys = emittedKeys;
        }

        public bool IsInTarget(string file)
            => !string.IsNullOrEmpty(file) && TargetFilesNormalized.Contains(AnalysisIssue.NormalizePath(file));

        public void Report(string ruleId, DumpToken locTok, string message)
            => Report(ruleId, locTok.File, locTok.LineNr, locTok.Column, message);

        public void Report(string ruleId, string filePath, int line, int column, string message)
        {
            if (line <= 0 || !IsInTarget(filePath))
                return;
            if (!RulesById.TryGetValue(ruleId, out var rule) || !rule.IsEnabled)
                return;

            string dedupKey = AnalysisIssue.MakePositionKey(ruleId, filePath, line, column);
            if (!EmittedKeys.Add(dedupKey))
                return;

            Issues.Add(new AnalysisIssue
            {
                RuleId = ruleId,
                Category = rule.Category,
                Severity = rule.Severity,
                Obligation = rule.Obligation,
                Title = rule.Title,
                Message = message,
                FilePath = Path.GetFullPath(filePath),
                Line = line,
                Column = column,
                IsInfoOrNote = false,
                RuleDetails = rule
            });
        }

        /// <summary>
        /// 전처리 전 매크로 이름 또는 원본 식별자를 반환한다.
        /// </summary>
        public string? GetMacroName(DumpToken tok)
        {
            if (!string.IsNullOrEmpty(tok.MacroName))
                return tok.MacroName;

            if (string.IsNullOrEmpty(tok.File) || tok.LineNr <= 0 || tok.Column <= 0)
                return null;

            string norm = AnalysisIssue.NormalizePath(tok.File);
            if (!RawIndex.TryGetValue(norm, out var fileIdx))
                return null;

            if (fileIdx.ByPosition.TryGetValue((tok.LineNr, tok.Column), out var rt) &&
                IsIdentifierName(rt.Str) &&
                !CKeywords.Contains(rt.Str) &&
                !string.Equals(rt.Str, tok.Str, StringComparison.Ordinal))
            {
                return rt.Str;
            }
            return null;
        }

        /// <summary>
        /// 토큰의 표시용 식별자(매크로 원문 우선, 없으면 토큰 문자열)를 반환한다.
        /// </summary>
        public string GetEffectiveSymbol(DumpToken tok)
            => GetMacroName(tok) ?? tok.Str;

        /// <summary>
        /// 문장 범위 [startLine..endLine] 직전(1~2줄 이내, 중간에 다른 코드 토큰 없음) 또는 같은 줄에 달린 주석 목록을 조회한다.
        /// </summary>
        public List<string> GetAssociatedComments(string file, int startLine, int endLine)
        {
            var comments = new List<string>();
            if (string.IsNullOrEmpty(file) || startLine <= 0)
                return comments;

            string norm = AnalysisIssue.NormalizePath(file);
            if (!RawIndex.TryGetValue(norm, out var fileIdx) || fileIdx.Comments.Count == 0)
                return comments;

            // 1) 직전 주석 (startLine - 1 또는 startLine - 2, 중간 줄에 코드 토큰이 없을 때만)
            DumpRawToken? preceding = null;
            for (int i = fileIdx.Comments.Count - 1; i >= 0; i--)
            {
                var c = fileIdx.Comments[i];
                if (c.LineNr >= startLine)
                    continue;
                if (c.LineNr < startLine - 2)
                    break;

                bool hasCodeBetween = false;
                for (int line = c.LineNr; line < startLine; line++)
                {
                    if (fileIdx.CodeTokensByLine.TryGetValue(line, out var codeList) &&
                        codeList.Any(ct => line > c.LineNr || ct.Column < c.Column))
                    {
                        // 같은 줄 앞쪽에 코드가 있었다면 이전 문장의 인라인 주석이므로 제외
                        if (line == c.LineNr && codeList.Any(ct => ct.Column < c.Column))
                        {
                            hasCodeBetween = true;
                            break;
                        }
                        if (line > c.LineNr && codeList.Count > 0)
                        {
                            hasCodeBetween = true;
                            break;
                        }
                    }
                }

                if (!hasCodeBetween)
                    preceding = c;
                break;
            }
            if (preceding != null)
                comments.Add(StripCommentDelimiters(preceding.Str));

            // 2) 인라인 주석 (startLine .. endLine)
            foreach (var c in fileIdx.Comments)
            {
                if (c.LineNr >= startLine && c.LineNr <= endLine)
                    comments.Add(StripCommentDelimiters(c.Str));
            }

            return comments;
        }
    }

    private static string StripCommentDelimiters(string raw)
    {
        if (raw.StartsWith("//", StringComparison.Ordinal))
            return raw.Substring(2).Trim();
        if (raw.StartsWith("/*", StringComparison.Ordinal))
        {
            string s = raw.Substring(2);
            if (s.EndsWith("*/", StringComparison.Ordinal))
                s = s.Substring(0, s.Length - 2);
            return s.Trim();
        }
        return raw.Trim();
    }

    // -------------------------------------------------------------------------
    // 함수/블록 단위 문장 파서
    // -------------------------------------------------------------------------

    private abstract class Stmt
    {
        public DumpToken FirstToken { get; init; } = null!;
        public int StartLine => FirstToken.LineNr;
        public int EndLine { get; init; }
        public string File => FirstToken.File;
    }

    private sealed class SimpleStmt : Stmt
    {
        public List<DumpToken> Tokens { get; init; } = new();
        public DumpToken? AstRoot { get; init; }
        public bool IsSplittedDecl { get; init; }
        public bool IsControlFlowBreak { get; init; }
        public bool IsLabel { get; init; }
    }

    private sealed class BlockStmt : Stmt
    {
        public List<Stmt> Statements { get; init; } = new();
    }

    private sealed class IfStmt : Stmt
    {
        public DumpToken? CondRoot { get; init; }
        public List<DumpToken> CondTokens { get; init; } = new();
        public List<Stmt> ThenStatements { get; init; } = new();
        public List<Stmt> ElseStatements { get; init; } = new();
        public IfStmt? ElseIf { get; init; }
    }

    private sealed class ForStmt : Stmt
    {
        public DumpToken? InitRoot { get; init; }
        public DumpToken? CondRoot { get; init; }
        public DumpToken? IncRoot { get; init; }
        public List<DumpToken> BodyTokens { get; init; } = new();
        public List<Stmt> BodyStatements { get; init; } = new();
    }

    private sealed class LoopStmt : Stmt
    {
        public List<Stmt> BodyStatements { get; init; } = new();
    }

    private sealed class SwitchSection
    {
        public DumpToken CaseToken { get; init; } = null!;
        public List<Stmt> Statements { get; init; } = new();
    }

    private sealed class SwitchStmt : Stmt
    {
        public List<SwitchSection> Sections { get; init; } = new();
    }

    private static List<(List<DumpToken> Tokens, HashSet<string> LoopVarKeys)> ExtractFunctionTokenRanges(DumpCfgUnit unit)
    {
        var result = new List<(List<DumpToken>, HashSet<string>)>();
        var funcScopes = unit.ScopeById.Values
            .Where(s => s.Type == "Function" && s.BodyStart != null && s.BodyEnd != null)
            .ToList();

        if (funcScopes.Count == 0)
        {
            if (unit.Tokens.Count > 0)
                result.Add((unit.Tokens, CollectLoopVarKeys(unit.Tokens)));
            return result;
        }

        foreach (var fs in funcScopes)
        {
            var bodyTokens = new List<DumpToken>();
            for (var t = fs.BodyStart!.Next; t != null && t != fs.BodyEnd; t = t.Next)
                bodyTokens.Add(t);

            if (bodyTokens.Count > 0)
                result.Add((bodyTokens, CollectLoopVarKeys(bodyTokens)));
        }

        return result;
    }

    private static List<Stmt> ParseStatements(IReadOnlyList<DumpToken> tokens)
    {
        int pos = 0;
        return ParseStatementRange(tokens, ref pos, tokens.Count);
    }

    private static List<Stmt> ParseStatementRange(IReadOnlyList<DumpToken> tokens, ref int pos, int limit)
    {
        var list = new List<Stmt>();
        while (pos < limit)
        {
            var tok = tokens[pos];
            if (tok.Str == "}")
                break;

            if (tok.Str == ";")
            {
                pos++;
                continue;
            }

            if (tok.Str == "{")
            {
                list.Add(ParseBlock(tokens, ref pos, limit));
                continue;
            }

            if (tok.Str == "if")
            {
                list.Add(ParseIf(tokens, ref pos, limit));
                continue;
            }

            if (tok.Str == "for")
            {
                list.Add(ParseFor(tokens, ref pos, limit));
                continue;
            }

            if (tok.Str == "while")
            {
                list.Add(ParseWhile(tokens, ref pos, limit));
                continue;
            }

            if (tok.Str == "do")
            {
                list.Add(ParseDoWhile(tokens, ref pos, limit));
                continue;
            }

            if (tok.Str == "switch")
            {
                list.Add(ParseSwitch(tokens, ref pos, limit));
                continue;
            }

            if ((tok.Str == "case" || tok.Str == "default") && TrySkipCaseLabel(tokens, ref pos, limit, out var labelStmt))
            {
                list.Add(labelStmt);
                continue;
            }

            // 일반 문장 또는 레이블(ident:)
            list.Add(ParseSimpleStatement(tokens, ref pos, limit));
        }
        return list;
    }

    private static BlockStmt ParseBlock(IReadOnlyList<DumpToken> tokens, ref int pos, int limit)
    {
        var openBrace = tokens[pos];
        pos++;
        int endIdx = FindMatchingClose(tokens, pos - 1, limit, "{", "}");
        int innerLimit = endIdx >= 0 ? endIdx : limit;
        var stmts = ParseStatementRange(tokens, ref pos, innerLimit);
        int endLine = openBrace.LineNr;
        if (endIdx >= 0)
        {
            endLine = tokens[endIdx].LineNr;
            pos = endIdx + 1;
        }
        return new BlockStmt { FirstToken = openBrace, EndLine = endLine, Statements = stmts };
    }

    private static List<Stmt> ParseSingleOrBlockBody(IReadOnlyList<DumpToken> tokens, ref int pos, int limit, out List<DumpToken> rawBodyTokens, out int endLine)
    {
        rawBodyTokens = new List<DumpToken>();
        if (pos >= limit)
        {
            endLine = 0;
            return new List<Stmt>();
        }

        if (tokens[pos].Str == "{")
        {
            int start = pos;
            var block = ParseBlock(tokens, ref pos, limit);
            for (int i = start + 1; i < pos - 1 && i < limit; i++)
                rawBodyTokens.Add(tokens[i]);
            endLine = block.EndLine;
            return block.Statements;
        }

        int before = pos;
        var single = ParseStatementRangeSingle(tokens, ref pos, limit);
        for (int i = before; i < pos && i < limit; i++)
            rawBodyTokens.Add(tokens[i]);
        endLine = single?.EndLine ?? tokens[before].LineNr;
        return single != null ? new List<Stmt> { single } : new List<Stmt>();
    }

    private static Stmt? ParseStatementRangeSingle(IReadOnlyList<DumpToken> tokens, ref int pos, int limit)
    {
        if (pos >= limit || tokens[pos].Str == "}")
            return null;
        var tok = tokens[pos];
        if (tok.Str == "{") return ParseBlock(tokens, ref pos, limit);
        if (tok.Str == "if") return ParseIf(tokens, ref pos, limit);
        if (tok.Str == "for") return ParseFor(tokens, ref pos, limit);
        if (tok.Str == "while") return ParseWhile(tokens, ref pos, limit);
        if (tok.Str == "do") return ParseDoWhile(tokens, ref pos, limit);
        if (tok.Str == "switch") return ParseSwitch(tokens, ref pos, limit);
        return ParseSimpleStatement(tokens, ref pos, limit);
    }

    private static IfStmt ParseIf(IReadOnlyList<DumpToken> tokens, ref int pos, int limit)
    {
        var ifTok = tokens[pos++];
        DumpToken? condRoot = null;
        var condTokens = new List<DumpToken>();

        if (pos < limit && tokens[pos].Str == "(")
        {
            var openParen = tokens[pos];
            int closeIdx = FindMatchingClose(tokens, pos, limit, "(", ")");
            if (closeIdx > pos)
            {
                for (int i = pos + 1; i < closeIdx; i++)
                    condTokens.Add(tokens[i]);
                condRoot = openParen.AstOperand2 ?? FindAstRoot(condTokens);
                pos = closeIdx + 1;
            }
        }

        var thenStmts = ParseSingleOrBlockBody(tokens, ref pos, limit, out _, out int endLine);
        var elseStmts = new List<Stmt>();
        IfStmt? elseIf = null;

        if (pos < limit && tokens[pos].Str == "else")
        {
            pos++;
            if (pos < limit && tokens[pos].Str == "if")
            {
                elseIf = ParseIf(tokens, ref pos, limit);
                endLine = elseIf.EndLine;
            }
            else if (pos < limit && tokens[pos].Str == "{" && tokens[pos].IsInsertedBrace)
            {
                // Cppcheck는 else if를 else { /*inserted*/ if (...) ... } 로 감싼다.
                var insertedBlock = ParseBlock(tokens, ref pos, limit);
                if (insertedBlock.Statements.Count == 1 && insertedBlock.Statements[0] is IfStmt nestedIf)
                    elseIf = nestedIf;
                else
                    elseStmts = insertedBlock.Statements;
                endLine = insertedBlock.EndLine;
            }
            else
            {
                elseStmts = ParseSingleOrBlockBody(tokens, ref pos, limit, out _, out int elseEnd);
                if (elseEnd > endLine)
                    endLine = elseEnd;
            }
        }

        return new IfStmt
        {
            FirstToken = ifTok,
            EndLine = Math.Max(ifTok.LineNr, endLine),
            CondRoot = condRoot,
            CondTokens = condTokens,
            ThenStatements = thenStmts,
            ElseStatements = elseStmts,
            ElseIf = elseIf
        };
    }

    private static ForStmt ParseFor(IReadOnlyList<DumpToken> tokens, ref int pos, int limit)
    {
        var forTok = tokens[pos++];
        DumpToken? initRoot = null;
        DumpToken? condRoot = null;
        DumpToken? incRoot = null;

        if (pos < limit && tokens[pos].Str == "(")
        {
            var openParen = tokens[pos];
            int closeIdx = FindMatchingClose(tokens, pos, limit, "(", ")");
            if (closeIdx > pos)
            {
                var firstSemi = openParen.AstOperand2;
                if (firstSemi != null && firstSemi.Str == ";")
                {
                    initRoot = firstSemi.AstOperand1;
                    var secondSemi = firstSemi.AstOperand2;
                    if (secondSemi != null && secondSemi.Str == ";")
                    {
                        condRoot = secondSemi.AstOperand1;
                        incRoot = secondSemi.AstOperand2;
                    }
                }
                else
                {
                    // 합성 테스트에서 AST ; 링크 없이 토큰 나열만 있는 경우 대비
                    ExtractForPartsFromTokens(tokens, pos + 1, closeIdx, out initRoot, out condRoot, out incRoot);
                }
                pos = closeIdx + 1;
            }
        }

        var bodyStmts = ParseSingleOrBlockBody(tokens, ref pos, limit, out var bodyTokens, out int endLine);
        return new ForStmt
        {
            FirstToken = forTok,
            EndLine = Math.Max(forTok.LineNr, endLine),
            InitRoot = initRoot,
            CondRoot = condRoot,
            IncRoot = incRoot,
            BodyTokens = bodyTokens,
            BodyStatements = bodyStmts
        };
    }

    private static void ExtractForPartsFromTokens(
        IReadOnlyList<DumpToken> tokens, int start, int end,
        out DumpToken? initRoot, out DumpToken? condRoot, out DumpToken? incRoot)
    {
        initRoot = condRoot = incRoot = null;
        var part = new List<DumpToken>();
        int section = 0;
        for (int i = start; i < end; i++)
        {
            if (tokens[i].Str == ";")
            {
                if (section == 0) initRoot = FindAstRoot(part);
                else if (section == 1) condRoot = FindAstRoot(part);
                part.Clear();
                section++;
            }
            else
            {
                part.Add(tokens[i]);
            }
        }
        if (section == 2)
            incRoot = FindAstRoot(part);
    }

    private static LoopStmt ParseWhile(IReadOnlyList<DumpToken> tokens, ref int pos, int limit)
    {
        var tok = tokens[pos++];
        if (pos < limit && tokens[pos].Str == "(")
        {
            int closeIdx = FindMatchingClose(tokens, pos, limit, "(", ")");
            if (closeIdx >= 0) pos = closeIdx + 1;
        }
        var body = ParseSingleOrBlockBody(tokens, ref pos, limit, out _, out int endLine);
        return new LoopStmt { FirstToken = tok, EndLine = Math.Max(tok.LineNr, endLine), BodyStatements = body };
    }

    private static LoopStmt ParseDoWhile(IReadOnlyList<DumpToken> tokens, ref int pos, int limit)
    {
        var tok = tokens[pos++];
        var body = ParseSingleOrBlockBody(tokens, ref pos, limit, out _, out int endLine);
        if (pos < limit && tokens[pos].Str == "while")
        {
            pos++;
            if (pos < limit && tokens[pos].Str == "(")
            {
                int closeIdx = FindMatchingClose(tokens, pos, limit, "(", ")");
                if (closeIdx >= 0) pos = closeIdx + 1;
            }
            if (pos < limit && tokens[pos].Str == ";")
            {
                endLine = tokens[pos].LineNr;
                pos++;
            }
        }
        return new LoopStmt { FirstToken = tok, EndLine = Math.Max(tok.LineNr, endLine), BodyStatements = body };
    }

    private static SwitchStmt ParseSwitch(IReadOnlyList<DumpToken> tokens, ref int pos, int limit)
    {
        var swTok = tokens[pos++];
        if (pos < limit && tokens[pos].Str == "(")
        {
            int closeIdx = FindMatchingClose(tokens, pos, limit, "(", ")");
            if (closeIdx >= 0) pos = closeIdx + 1;
        }

        var sections = new List<SwitchSection>();
        int endLine = swTok.LineNr;
        if (pos < limit && tokens[pos].Str == "{")
        {
            var block = ParseBlock(tokens, ref pos, limit);
            endLine = block.EndLine;
            SwitchSection? current = null;
            foreach (var s in block.Statements)
            {
                if (s is SimpleStmt ss && ss.IsLabel && (ss.FirstToken.Str == "case" || ss.FirstToken.Str == "default"))
                {
                    current = new SwitchSection { CaseToken = ss.FirstToken };
                    sections.Add(current);
                }
                else if (current != null)
                {
                    current.Statements.Add(s);
                }
            }
        }

        return new SwitchStmt { FirstToken = swTok, EndLine = endLine, Sections = sections };
    }

    private static bool TrySkipCaseLabel(IReadOnlyList<DumpToken> tokens, ref int pos, int limit, out SimpleStmt labelStmt)
    {
        var first = tokens[pos];
        var slice = new List<DumpToken>();
        int i = pos;
        int parenDepth = 0;
        while (i < limit)
        {
            var t = tokens[i];
            slice.Add(t);
            if (t.Str == "(") parenDepth++;
            else if (t.Str == ")" && parenDepth > 0) parenDepth--;
            else if (t.Str == ":" && parenDepth == 0)
            {
                pos = i + 1;
                labelStmt = new SimpleStmt
                {
                    FirstToken = first,
                    EndLine = t.LineNr,
                    Tokens = slice,
                    IsLabel = true,
                    IsControlFlowBreak = true
                };
                return true;
            }
            else if (t.Str is ";" or "{" or "}")
            {
                break;
            }
            i++;
        }
        labelStmt = null!;
        return false;
    }

    private static SimpleStmt ParseSimpleStatement(IReadOnlyList<DumpToken> tokens, ref int pos, int limit)
    {
        var first = tokens[pos];
        var slice = new List<DumpToken>();
        int parenDepth = 0;
        int bracketDepth = 0;
        bool isSplittedDecl = false;
        bool isLabel = false;
        int endLine = first.LineNr;

        while (pos < limit)
        {
            var t = tokens[pos];
            if (t.Str == "}" && parenDepth == 0 && bracketDepth == 0)
                break;

            slice.Add(t);
            endLine = t.LineNr;
            pos++;

            if (t.Str == "(") parenDepth++;
            else if (t.Str == ")" && parenDepth > 0) parenDepth--;
            else if (t.Str == "[") bracketDepth++;
            else if (t.Str == "]" && bracketDepth > 0) bracketDepth--;
            else if (t.Str == ";" && parenDepth == 0 && bracketDepth == 0)
            {
                isSplittedDecl = t.IsSplittedVarDeclEq;
                break;
            }
            else if (t.Str == ":" && parenDepth == 0 && bracketDepth == 0 && slice.Count == 2 && IsIdentifierName(slice[0].Str))
            {
                isLabel = true;
                break;
            }
        }

        bool isBreak = isLabel || first.Str is "return" or "goto" or "break" or "continue";
        var root = FindAstRoot(slice);
        return new SimpleStmt
        {
            FirstToken = first,
            EndLine = endLine,
            Tokens = slice,
            AstRoot = root,
            IsSplittedDecl = isSplittedDecl,
            IsControlFlowBreak = isBreak,
            IsLabel = isLabel
        };
    }

    private static int FindMatchingClose(IReadOnlyList<DumpToken> tokens, int openIdx, int limit, string openStr, string closeStr)
    {
        var openTok = tokens[openIdx];
        if (openTok.Link != null)
        {
            for (int i = openIdx + 1; i < limit; i++)
            {
                if (ReferenceEquals(tokens[i], openTok.Link))
                    return i;
            }
        }

        int depth = 0;
        for (int i = openIdx; i < limit; i++)
        {
            if (tokens[i].Str == openStr) depth++;
            else if (tokens[i].Str == closeStr)
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static DumpToken? FindAstRoot(IReadOnlyList<DumpToken> slice)
    {
        if (slice.Count == 0)
            return null;

        var sliceSet = new HashSet<DumpToken>(slice);
        var hasParentInSlice = new HashSet<DumpToken>();
        foreach (var t in slice)
        {
            if (t.AstOperand1 != null && sliceSet.Contains(t.AstOperand1))
                hasParentInSlice.Add(t.AstOperand1);
            if (t.AstOperand2 != null && sliceSet.Contains(t.AstOperand2))
                hasParentInSlice.Add(t.AstOperand2);
        }

        // 우선 연산자/호출/return 루트 반환
        foreach (var t in slice)
        {
            if (hasParentInSlice.Contains(t))
                continue;
            if (t.AstOperand1 != null || t.AstOperand2 != null)
                return t;
        }

        foreach (var t in slice)
        {
            if (!hasParentInSlice.Contains(t) && (t.Type == "name" || t.IsIntLiteral || t.IsFloatLiteral))
                return t;
        }
        return null;
    }

    // -------------------------------------------------------------------------
    // 공통 AST / 식별자 번호 패턴 헬퍼
    // -------------------------------------------------------------------------

    private static bool IsIdentifierName(string s)
    {
        if (string.IsNullOrEmpty(s))
            return false;
        char first = s[0];
        if (!(char.IsLetter(first) || first == '_'))
            return false;
        for (int i = 1; i < s.Length; i++)
        {
            char c = s[i];
            if (!(char.IsLetterOrDigit(c) || c == '_'))
                return false;
        }
        return true;
    }

    private static bool IsExcludedTypeOrKeyword(string s)
        => string.IsNullOrEmpty(s) || CKeywords.Contains(s) || StandardTypeRegex.IsMatch(s);

    private readonly record struct NumberSlot(string Pattern, int Number, bool IsArrayIndex, bool IsIdentifier, string RawName, DumpToken Token);

    private static List<NumberSlot> ExtractIdentifierNumbers(string name, DumpToken tok, bool isArrayIndex)
    {
        var results = new List<NumberSlot>();
        if (IsExcludedTypeOrKeyword(name))
            return results;

        var matches = DigitGroupRegex.Matches(name);
        foreach (Match m in matches)
        {
            if (!int.TryParse(m.Value, out int num))
                continue;
            string pattern = string.Concat(name.AsSpan(0, m.Index), "#", name.AsSpan(m.Index + m.Length));
            results.Add(new NumberSlot(pattern, num, isArrayIndex, IsIdentifier: true, name, tok));
        }
        return results;
    }

    private static bool HasSideEffects(DumpToken? root)
    {
        if (root == null)
            return false;

        bool found = false;
        WalkAst(root, t =>
        {
            if (t.IsAssignmentOp || t.Str is "++" or "--")
                found = true;
            else if (t.Str == "(" && !t.IsCast)
                found = true;
        });
        return found;
    }

    private static void WalkAst(DumpToken? root, Action<DumpToken> visitor)
    {
        if (root == null)
            return;
        var visited = new HashSet<DumpToken>();
        void Visit(DumpToken? t)
        {
            if (t == null || !visited.Add(t))
                return;
            visitor(t);
            Visit(t.AstOperand1);
            Visit(t.AstOperand2);
        }
        Visit(root);
    }

    private static bool IsSameVariable(DumpToken a, DumpToken b)
    {
        if (!string.IsNullOrEmpty(a.VariableId) && !string.IsNullOrEmpty(b.VariableId))
            return string.Equals(a.VariableId, b.VariableId, StringComparison.Ordinal);
        if (a.Variable != null && b.Variable != null && !string.IsNullOrEmpty(a.Variable.Id) && !string.IsNullOrEmpty(b.Variable.Id))
            return string.Equals(a.Variable.Id, b.Variable.Id, StringComparison.Ordinal);
        return string.Equals(a.Str, b.Str, StringComparison.Ordinal);
    }

    private static string GetVariableKey(DumpToken tok)
    {
        if (!string.IsNullOrEmpty(tok.VariableId))
            return "id:" + tok.VariableId;
        if (tok.Variable != null && !string.IsNullOrEmpty(tok.Variable.Id))
            return "id:" + tok.Variable.Id;
        return "name:" + tok.Str;
    }

    private static bool AreAstEqual(DumpToken? a, DumpToken? b, AnalysisContext ctx)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;

        string? macroA = ctx.GetMacroName(a);
        string? macroB = ctx.GetMacroName(b);
        if (macroA != null || macroB != null)
            return string.Equals(macroA ?? a.Str, macroB ?? b.Str, StringComparison.Ordinal);

        if (!string.Equals(a.Str, b.Str, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrEmpty(a.VariableId) && !string.IsNullOrEmpty(b.VariableId) &&
            !string.Equals(a.VariableId, b.VariableId, StringComparison.Ordinal))
        {
            return false;
        }

        return AreAstEqual(a.AstOperand1, b.AstOperand1, ctx) &&
               AreAstEqual(a.AstOperand2, b.AstOperand2, ctx);
    }

    /// <summary>
    /// AST 골격 문자열(숫자 번호·매크로/변수 식별자 차이를 정규화한 구조)과 슬롯 목록을 추출한다.
    /// </summary>
    private static string BuildNormalizedSkeleton(
        DumpToken? root,
        AnalysisContext ctx,
        bool normalizeCallNames,
        List<DumpToken>? slotTokens = null)
    {
        if (root == null)
            return string.Empty;

        var sb = new StringBuilder();
        var visited = new HashSet<DumpToken>();

        void Visit(DumpToken? t, string? parentMacro)
        {
            if (t == null || !visited.Add(t))
            {
                sb.Append("_");
                return;
            }

            string? macro = ctx.GetMacroName(t);
            if (!string.IsNullOrEmpty(macro) && !string.Equals(macro, parentMacro, StringComparison.Ordinal))
            {
                slotTokens?.Add(t);
                sb.Append("$M");
                return;
            }

            if (t.Str == "(" && !t.IsCast && t.AstOperand1 != null)
            {
                string fnName = t.AstOperand1.Str;
                if (normalizeCallNames)
                {
                    slotTokens?.Add(t.AstOperand1);
                    sb.Append("CALL(").Append(NormalizeDigits(fnName)).Append(',');
                }
                else
                {
                    sb.Append("CALL(").Append(fnName).Append(',');
                }
                Visit(t.AstOperand2, macro);
                sb.Append(')');
                return;
            }

            if (t.Str == "[" && t.AstOperand1 != null && t.AstOperand2 != null)
            {
                sb.Append("IDX(");
                Visit(t.AstOperand1, macro);
                sb.Append(',');
                if (t.AstOperand2.IsIntLiteral)
                {
                    slotTokens?.Add(t.AstOperand2);
                    sb.Append("$I");
                }
                else
                {
                    Visit(t.AstOperand2, macro);
                }
                sb.Append(')');
                return;
            }

            if (t.Str is "." or "->" && t.AstOperand1 != null && t.AstOperand2 != null)
            {
                sb.Append('(').Append(t.Str).Append(' ');
                Visit(t.AstOperand1, macro);
                sb.Append(' ');
                if (normalizeCallNames)
                {
                    slotTokens?.Add(t.AstOperand2);
                    sb.Append("$FLD:").Append(NormalizeDigits(t.AstOperand2.Str));
                }
                else
                {
                    sb.Append(t.AstOperand2.Str);
                }
                sb.Append(')');
                return;
            }

            if (t.AstOperand1 == null && t.AstOperand2 == null)
            {
                if (t.IsIntLiteral || t.IsFloatLiteral)
                {
                    sb.Append("$N:").Append(t.Str);
                }
                else if (IsIdentifierName(t.Str) && !IsExcludedTypeOrKeyword(t.Str))
                {
                    slotTokens?.Add(t);
                    sb.Append("$ID");
                }
                else
                {
                    sb.Append(t.Str);
                }
                return;
            }

            sb.Append('(').Append(t.Str).Append(' ');
            Visit(t.AstOperand1, macro);
            if (t.AstOperand2 != null)
            {
                sb.Append(' ');
                Visit(t.AstOperand2, macro);
            }
            sb.Append(')');
        }

        Visit(root, null);
        return sb.ToString();
    }

    private static string NormalizeDigits(string s)
        => DigitGroupRegex.Replace(s, "#");

    // -------------------------------------------------------------------------
    // 규칙 1: copyPasteMacroCommentMismatch
    // -------------------------------------------------------------------------

    private static void AnalyzeMacroCommentMismatches(
        List<Stmt> stmts,
        AnalysisContext ctx,
        HashSet<(string FileNorm, int Line)> reportedLines)
    {
        for (int i = 0; i < stmts.Count; i++)
        {
            RecurseSubStatements(stmts[i], sub => AnalyzeMacroCommentMismatches(sub, ctx, reportedLines));
        }

        var execStmts = stmts.OfType<SimpleStmt>()
            .Where(s => !s.IsSplittedDecl && !s.IsControlFlowBreak && s.AstRoot != null)
            .ToList();

        // 윈도우 크기 L = 1..4로 인접 문장/블록 쌍 비교
        for (int len = 4; len >= 1; len--)
        {
            for (int i = 0; i + (2 * len) <= execStmts.Count; i++)
            {
                var b1 = execStmts.GetRange(i, len);
                var b2 = execStmts.GetRange(i + len, len);

                if (!BlocksHaveMatchingSkeletons(b1, b2, ctx, normalizeCallNames: true))
                    continue;

                // 다중 문장 블록(len > 1)은 첫 문장에 주석이 있거나 블록 내부에 문맥 변화가 있는 경우 우선 검사
                var transitions = ExtractCommentNumberTransitions(b1, b2, ctx);
                bool hasCommentTransition = transitions.Count > 0;

                if (!hasCommentTransition)
                {
                    // 주석이 없더라도 주변 식별자/매크로에 k -> m 전환이 있으면서 동일 블록 내 #define 매크로가 k로 남은 경우 확인
                    CheckContextIdentifierMacroMismatch(b1, b2, ctx, reportedLines);
                    continue;
                }

                // 주석에서 k -> m 전환이 확인되었을 때, b1/b2의 대응 슬롯 중 k를 포함한 매크로/식별자가 m으로 안 바뀌고 k 그대로 남은 슬롯 검출
                for (int sIdx = 0; sIdx < len; sIdx++)
                {
                    var s1 = b1[sIdx];
                    var s2 = b2[sIdx];
                    var slots1 = new List<DumpToken>();
                    var slots2 = new List<DumpToken>();
                    BuildNormalizedSkeleton(s1.AstRoot, ctx, normalizeCallNames: true, slots1);
                    BuildNormalizedSkeleton(s2.AstRoot, ctx, normalizeCallNames: true, slots2);

                    if (slots1.Count != slots2.Count)
                        continue;

                    for (int k = 0; k < slots1.Count; k++)
                    {
                        var t1 = slots1[k];
                        var t2 = slots2[k];
                        string sym1 = ctx.GetEffectiveSymbol(t1);
                        string sym2 = ctx.GetEffectiveSymbol(t2);
                        if (!string.Equals(sym1, sym2, StringComparison.Ordinal))
                            continue;
                        if (t1.IsIntLiteral && string.IsNullOrEmpty(ctx.GetMacroName(t1)))
                            continue;
                        if (IsExcludedTypeOrKeyword(sym1))
                            continue;

                        var numInfos = ExtractIdentifierNumbers(sym1, t1, isArrayIndex: false);
                        foreach (var info in numInfos)
                        {
                            if (transitions.TryGetValue(info.Number, out int targetNum) && targetNum != info.Number)
                            {
                                string normFile = AnalysisIssue.NormalizePath(t2.File);
                                reportedLines.Add((normFile, s2.StartLine));
                                ctx.Report(
                                    RuleMacroCommentMismatch,
                                    t2,
                                    $"복사 코드의 주석·매크로 불일치: 주석(또는 문맥)의 번호는 {info.Number}에서 {targetNum}(으)로 변경되었으나, 식별자/매크로 '{sym2}'는 이전 번호({info.Number}) 그대로 남아 있습니다.");
                                break;
                            }
                        }
                    }
                }
            }
        }
    }

    private static bool BlocksHaveMatchingSkeletons(
        List<SimpleStmt> b1,
        List<SimpleStmt> b2,
        AnalysisContext ctx,
        bool normalizeCallNames)
    {
        if (b1.Count != b2.Count || b1.Count == 0)
            return false;

        for (int i = 0; i < b1.Count; i++)
        {
            if (b1[i].AstRoot == null || b2[i].AstRoot == null)
                return false;

            string sk1 = BuildNormalizedSkeleton(b1[i].AstRoot, ctx, normalizeCallNames);
            string sk2 = BuildNormalizedSkeleton(b2[i].AstRoot, ctx, normalizeCallNames);
            if (!string.Equals(sk1, sk2, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static Dictionary<int, int> ExtractCommentNumberTransitions(
        List<SimpleStmt> b1,
        List<SimpleStmt> b2,
        AnalysisContext ctx)
    {
        var transitions = new Dictionary<int, int>();

        // 블록 전체 대표 주석(첫 문장 직전 주석) 및 각 대응 문장별 주석 비교
        for (int idx = 0; idx < b1.Count; idx++)
        {
            var c1List = ctx.GetAssociatedComments(b1[idx].File, b1[idx].StartLine, b1[idx].EndLine);
            var c2List = ctx.GetAssociatedComments(b2[idx].File, b2[idx].StartLine, b2[idx].EndLine);

            int pairCount = Math.Min(c1List.Count, c2List.Count);
            for (int c = 0; c < pairCount; c++)
            {
                AddCommentTransitions(c1List[c], c2List[c], transitions);
            }
        }

        return transitions;
    }

    private static void AddCommentTransitions(string c1, string c2, Dictionary<int, int> transitions)
    {
        // 1) 주석 골격이 숫자만 제외하고 동일한 경우 (예: "장치 1 설정" -> "장치 2 설정", "UART1 초기화" -> "UART2 초기화")
        if (string.Equals(NormalizeDigits(c1), NormalizeDigits(c2), StringComparison.OrdinalIgnoreCase))
        {
            var m1 = DigitGroupRegex.Matches(c1);
            var m2 = DigitGroupRegex.Matches(c2);
            if (m1.Count == m2.Count)
            {
                for (int i = 0; i < m1.Count; i++)
                {
                    if (int.TryParse(m1[i].Value, out int n1) &&
                        int.TryParse(m2[i].Value, out int n2) &&
                        n1 != n2)
                    {
                        transitions.TryAdd(n1, n2);
                    }
                }
            }
            return;
        }

        // 2) 주석 문구가 다소 다르더라도 동일 태그 접두사(CH1->CH2, UART0->UART1, 장치1->장치2)가 매칭되는 경우
        var tags1 = ExtractCommentTags(c1);
        var tags2 = ExtractCommentTags(c2);
        foreach (var kvp in tags1)
        {
            if (GenericStepCommentTagRegex.IsMatch(kvp.Key))
                continue;
            if (tags2.TryGetValue(kvp.Key, out int n2) && kvp.Value != n2)
                transitions.TryAdd(kvp.Value, n2);
        }
    }

    private static Dictionary<string, int> ExtractCommentTags(string comment)
    {
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in CommentTaggedNumberRegex.Matches(comment))
        {
            string tag = m.Groups["tag"].Value;
            if (int.TryParse(m.Groups["num"].Value, out int num))
                dict.TryAdd(tag, num);
        }
        return dict;
    }

    private static void CheckContextIdentifierMacroMismatch(
        List<SimpleStmt> b1,
        List<SimpleStmt> b2,
        AnalysisContext ctx,
        HashSet<(string FileNorm, int Line)> reportedLines)
    {
        // 블록 내 다른 식별자가 k -> m으로 바뀌었는데, #define 매크로 식별자(rawtokens/MacroName)가 k 그대로 남은 경우 검출
        var changedTransitions = new Dictionary<int, List<string>>();
        var unchangedMacros = new List<(DumpToken Tok, string Name, SimpleStmt Stmt)>();

        for (int sIdx = 0; sIdx < b1.Count; sIdx++)
        {
            var slots1 = new List<DumpToken>();
            var slots2 = new List<DumpToken>();
            BuildNormalizedSkeleton(b1[sIdx].AstRoot, ctx, normalizeCallNames: true, slots1);
            BuildNormalizedSkeleton(b2[sIdx].AstRoot, ctx, normalizeCallNames: true, slots2);
            if (slots1.Count != slots2.Count)
                continue;

            for (int k = 0; k < slots1.Count; k++)
            {
                var t1 = slots1[k];
                var t2 = slots2[k];
                string sym1 = ctx.GetEffectiveSymbol(t1);
                string sym2 = ctx.GetEffectiveSymbol(t2);
                if (IsExcludedTypeOrKeyword(sym1) || IsExcludedTypeOrKeyword(sym2))
                    continue;

                if (!string.Equals(sym1, sym2, StringComparison.Ordinal))
                {
                    if (string.Equals(NormalizeDigits(sym1), NormalizeDigits(sym2), StringComparison.Ordinal))
                    {
                        var nums1 = ExtractIdentifierNumbers(sym1, t1, false);
                        var nums2 = ExtractIdentifierNumbers(sym2, t2, false);
                        for (int n = 0; n < Math.Min(nums1.Count, nums2.Count); n++)
                        {
                            if (nums1[n].Number != nums2[n].Number)
                            {
                                if (!changedTransitions.TryGetValue(nums1[n].Number, out var stems))
                                {
                                    stems = new List<string>();
                                    changedTransitions[nums1[n].Number] = stems;
                                }
                                stems.Add(sym1);
                            }
                        }
                    }
                }
                else
                {
                    bool isMacro = !string.IsNullOrEmpty(ctx.GetMacroName(t2));
                    if (isMacro)
                        unchangedMacros.Add((t2, sym2, b2[sIdx]));
                }
            }
        }

        if (changedTransitions.Count == 0 || unchangedMacros.Count == 0)
            return;

        // 단순 1문장 'lhs = f(MACRO)' 형태는 주석이 없으면 Rule 2(copyPasteSequenceMismatch)가 전담
        if (b2.Count == 1 && b2[0].AstRoot is { IsAssignmentOp: true })
            return;

        foreach (var (tok, name, stmt) in unchangedMacros)
        {
            foreach (var numInfo in ExtractIdentifierNumbers(name, tok, false))
            {
                if (!changedTransitions.TryGetValue(numInfo.Number, out var changedStems))
                    continue;

                if (b2.Count == 1 && !SharesAlphaPrefixOrToken(name, changedStems))
                    continue;

                string normFile = AnalysisIssue.NormalizePath(tok.File);
                reportedLines.Add((normFile, stmt.StartLine));
                ctx.Report(
                    RuleMacroCommentMismatch,
                    tok,
                    $"복사 코드의 문맥·매크로 불일치: 주변 식별자('{changedStems[0]}')는 번호가 변경되었으나, 매크로 '{name}'는 이전 번호({numInfo.Number}) 그대로 유지되어 있습니다.");
                break;
            }
        }
    }

    private static bool SharesAlphaPrefixOrToken(string macroName, List<string> changedNames)
    {
        var macroTokens = SplitAlphaWords(macroName);
        foreach (var changed in changedNames)
        {
            var changedTokens = SplitAlphaWords(changed);
            if (macroTokens.Overlaps(changedTokens))
                return true;
        }
        return false;
    }

    private static HashSet<string> SplitAlphaWords(string name)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in name.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            string alpha = DigitGroupRegex.Replace(part, string.Empty);
            if (alpha.Length >= 2)
                set.Add(alpha);
        }
        return set;
    }

    // -------------------------------------------------------------------------
    // 규칙 2: copyPasteSequenceMismatch
    // -------------------------------------------------------------------------

    private static void AnalyzeSequenceMismatches(
        List<Stmt> stmts,
        AnalysisContext ctx,
        HashSet<(string FileNorm, int Line)> macroReportedLines)
    {
        foreach (var s in stmts)
        {
            RecurseSubStatements(s, sub => AnalyzeSequenceMismatches(sub, ctx, macroReportedLines));
        }

        var execStmts = stmts.OfType<SimpleStmt>()
            .Where(s => !s.IsSplittedDecl && !s.IsControlFlowBreak && s.AstRoot != null)
            .ToList();

        for (int i = 1; i < execStmts.Count; i++)
        {
            var prev = execStmts[i - 1];
            var curr = execStmts[i];

            string normFile = AnalysisIssue.NormalizePath(curr.File);
            if (macroReportedLines.Contains((normFile, curr.StartLine)))
                continue;

            string skPrev = BuildNormalizedSkeleton(prev.AstRoot, ctx, normalizeCallNames: true);
            string skCurr = BuildNormalizedSkeleton(curr.AstRoot, ctx, normalizeCallNames: true);
            if (string.IsNullOrEmpty(skPrev) || !string.Equals(skPrev, skCurr, StringComparison.Ordinal))
                continue;

            if (prev.AstRoot!.IsAssignmentOp && curr.AstRoot!.IsAssignmentOp &&
                prev.AstRoot.Str == "=" && curr.AstRoot.Str == "=")
            {
                CheckAssignmentSequenceMismatch(execStmts, i, ctx);
            }
            else if (prev.AstRoot!.Str == "(" && curr.AstRoot!.Str == "(" && !prev.AstRoot.IsCast && !curr.AstRoot.IsCast)
            {
                CheckCallSequenceMismatch(execStmts, i, ctx);
            }
        }
    }

    private static void CheckAssignmentSequenceMismatch(List<SimpleStmt> execStmts, int currIdx, AnalysisContext ctx)
    {
        var prev = execStmts[currIdx - 1];
        var curr = execStmts[currIdx];

        var prevLhsSlots = CollectSequenceSlots(prev.AstRoot!.AstOperand1, ctx, allowBareLiterals: false);
        var prevRhsSlots = CollectSequenceSlots(prev.AstRoot.AstOperand2, ctx, allowBareLiterals: false);
        var currLhsSlots = CollectSequenceSlots(curr.AstRoot!.AstOperand1, ctx, allowBareLiterals: false);
        var currRhsSlots = CollectSequenceSlots(curr.AstRoot.AstOperand2, ctx, allowBareLiterals: false);

        if (prevLhsSlots.Count != currLhsSlots.Count || prevRhsSlots.Count != currRhsSlots.Count)
            return;
        if (prevLhsSlots.Count == 0 || prevRhsSlots.Count == 0)
            return;

        // 2단계 이상 선행 짝(예: arr[0]=b[0]; arr[1]=b[1];) 존재 여부 확인
        bool hasPriorChain = currIdx >= 2 &&
            string.Equals(
                BuildNormalizedSkeleton(execStmts[currIdx - 2].AstRoot, ctx, normalizeCallNames: true),
                BuildNormalizedSkeleton(prev.AstRoot, ctx, normalizeCallNames: true),
                StringComparison.Ordinal);

        // LHS가 k1 -> k2로 바뀌었으나 RHS의 어느 슬롯도 k1 -> k2로 바뀌지 않고 k1 그대로 남은 경우
        CheckSidePairMismatch(prevLhsSlots, currLhsSlots, prevRhsSlots, currRhsSlots, curr.AstRoot, hasPriorChain, execStmts, currIdx, ctx);
        // 또는 RHS가 k1 -> k2로 바뀌었으나 LHS의 어느 슬롯도 k1 -> k2로 바뀌지 않고 k1 그대로 남은 경우
        CheckSidePairMismatch(prevRhsSlots, currRhsSlots, prevLhsSlots, currLhsSlots, curr.AstRoot, hasPriorChain, execStmts, currIdx, ctx);
    }

    private static void CheckSidePairMismatch(
        List<NumberSlot> prevChangedSide,
        List<NumberSlot> currChangedSide,
        List<NumberSlot> prevFixedSide,
        List<NumberSlot> currFixedSide,
        DumpToken reportTok,
        bool hasPriorChain,
        List<SimpleStmt> execStmts,
        int currIdx,
        AnalysisContext ctx)
    {
        for (int a = 0; a < prevChangedSide.Count; a++)
        {
            var pA = prevChangedSide[a];
            var cA = currChangedSide[a];
            if (!string.Equals(pA.Pattern, cA.Pattern, StringComparison.Ordinal) || pA.Number == cA.Number)
                continue;

            int k1 = pA.Number;
            int k2 = cA.Number;

            // 반대편에 k1 -> k2로 함께 업데이트된 슬롯이 이미 있으면 정상 변경으로 간주
            bool anyFixedSideUpdated = false;
            for (int b = 0; b < prevFixedSide.Count; b++)
            {
                if (string.Equals(prevFixedSide[b].Pattern, currFixedSide[b].Pattern, StringComparison.Ordinal) &&
                    prevFixedSide[b].Number == k1 && currFixedSide[b].Number == k2)
                {
                    anyFixedSideUpdated = true;
                    break;
                }
            }
            if (anyFixedSideUpdated)
                continue;

            for (int b = 0; b < prevFixedSide.Count; b++)
            {
                var pB = prevFixedSide[b];
                var cB = currFixedSide[b];
                if (!string.Equals(pB.RawName, cB.RawName, StringComparison.Ordinal))
                    continue;
                if (pB.Number != k1)
                    continue;

                // 오탐 방지: arr[0] = def[0]; arr[1] = def[0];처럼 k1 == 0인 배열 리터럴 인덱스 단독 2줄 비교는
                // 선행 체인(arr[0]=b[0]; arr[1]=b[1]; arr[2]=b[1]; 등)이 없다면 고정 기본값 접근일 수 있으므로 제외
                if (!pB.IsIdentifier && k1 == 0 && !HasEarlierMatchedStep(execStmts, currIdx - 1, a, b, ctx))
                    continue;

                _ = hasPriorChain;
                ctx.Report(
                    RuleSequenceMismatch,
                    reportTok,
                    $"연속 대입의 인덱스/번호 불일치: 직전 문장에서는 '{pA.RawName}'와(과) '{pB.RawName}'의 번호가 {k1}로 짝을 이루었으나, 현재 문장에서는 '{cA.RawName}'({k2})로 바뀐 반면 '{cB.RawName}'는 {k1} 그대로 남아 있습니다.");
                return;
            }
        }
    }

    private static bool HasEarlierMatchedStep(List<SimpleStmt> execStmts, int prevIdx, int changedSlotIdx, int fixedSlotIdx, AnalysisContext ctx)
    {
        if (prevIdx < 1)
            return false;

        var earlier = execStmts[prevIdx - 1];
        var prev = execStmts[prevIdx];
        if (earlier.AstRoot == null || prev.AstRoot == null)
            return false;

        string sk1 = BuildNormalizedSkeleton(earlier.AstRoot, ctx, normalizeCallNames: true);
        string sk2 = BuildNormalizedSkeleton(prev.AstRoot, ctx, normalizeCallNames: true);
        if (!string.Equals(sk1, sk2, StringComparison.Ordinal))
            return false;

        var eLhs = CollectSequenceSlots(earlier.AstRoot.AstOperand1, ctx, false);
        var eRhs = CollectSequenceSlots(earlier.AstRoot.AstOperand2, ctx, false);
        var pLhs = CollectSequenceSlots(prev.AstRoot.AstOperand1, ctx, false);
        var pRhs = CollectSequenceSlots(prev.AstRoot.AstOperand2, ctx, false);
        if (changedSlotIdx < eLhs.Count && fixedSlotIdx < eRhs.Count &&
            changedSlotIdx < pLhs.Count && fixedSlotIdx < pRhs.Count)
        {
            return eLhs[changedSlotIdx].Number == eRhs[fixedSlotIdx].Number &&
                   eLhs[changedSlotIdx].Number != pLhs[changedSlotIdx].Number;
        }
        return false;
    }

    private static bool HasEarlierMatchedCallStep(List<SimpleStmt> execStmts, int prevIdx, int slotA, int slotB, AnalysisContext ctx)
    {
        if (prevIdx < 1)
            return false;

        var earlier = execStmts[prevIdx - 1];
        var prev = execStmts[prevIdx];
        if (earlier.AstRoot == null || prev.AstRoot == null)
            return false;

        string sk1 = BuildNormalizedSkeleton(earlier.AstRoot, ctx, normalizeCallNames: true);
        string sk2 = BuildNormalizedSkeleton(prev.AstRoot, ctx, normalizeCallNames: true);
        if (!string.Equals(sk1, sk2, StringComparison.Ordinal))
            return false;

        var eSlots = CollectSequenceSlots(earlier.AstRoot, ctx, allowBareLiterals: false);
        var pSlots = CollectSequenceSlots(prev.AstRoot, ctx, allowBareLiterals: false);
        if (slotA < eSlots.Count && slotB < eSlots.Count && slotA < pSlots.Count && slotB < pSlots.Count)
        {
            return eSlots[slotA].Number == eSlots[slotB].Number &&
                   eSlots[slotA].Number != pSlots[slotA].Number;
        }
        return false;
    }

    private static void CheckCallSequenceMismatch(List<SimpleStmt> execStmts, int currIdx, AnalysisContext ctx)
    {
        var prev = execStmts[currIdx - 1];
        var curr = execStmts[currIdx];

        var prevSlots = CollectSequenceSlots(prev.AstRoot, ctx, allowBareLiterals: false);
        var currSlots = CollectSequenceSlots(curr.AstRoot, ctx, allowBareLiterals: false);
        if (prevSlots.Count != currSlots.Count || prevSlots.Count < 2)
            return;

        for (int a = 0; a < prevSlots.Count; a++)
        {
            var pA = prevSlots[a];
            var cA = currSlots[a];
            if (!string.Equals(pA.Pattern, cA.Pattern, StringComparison.Ordinal) || pA.Number == cA.Number)
                continue;

            int k1 = pA.Number;
            int k2 = cA.Number;

            for (int b = 0; b < prevSlots.Count; b++)
            {
                if (b == a) continue;
                var pB = prevSlots[b];
                var cB = currSlots[b];
                if (!string.Equals(pB.RawName, cB.RawName, StringComparison.Ordinal))
                    continue;
                if (pB.Number != k1)
                    continue;

                if (!pB.IsIdentifier && k1 == 0)
                    continue;

                // 단발성 2줄 함수 호출에서 무관한 상수 매크로(예: set_pin(PIN1, MODE1); set_pin(PIN2, MODE1);) 오탐 방지:
                // 배열 인덱스이거나, 변수(비매크로)이거나, 접두사를 공유하거나, 직전에도 동일 패턴이 반복된 경우만 보고
                bool isMacroOrUpperConst = !string.IsNullOrEmpty(ctx.GetMacroName(pB.Token)) || pB.RawName.All(c => !char.IsLower(c));
                if (pB.IsIdentifier && isMacroOrUpperConst && !pB.IsArrayIndex && !pA.IsArrayIndex &&
                    !SharesAlphaPrefixOrToken(pB.RawName, new List<string> { pA.RawName }) &&
                    !HasEarlierMatchedCallStep(execStmts, currIdx - 1, a, b, ctx))
                {
                    continue;
                }

                ctx.Report(
                    RuleSequenceMismatch,
                    curr.AstRoot!,
                    $"연속 호출의 인덱스/번호 불일치: 직전 호출에서는 '{pA.RawName}'와(과) '{pB.RawName}'의 번호가 {k1}로 짝을 이루었으나, 현재 호출에서는 '{cA.RawName}'({k2})로 바뀐 반면 '{cB.RawName}'는 {k1} 그대로 남아 있습니다.");
                return;
            }
        }
    }

    private static List<NumberSlot> CollectSequenceSlots(DumpToken? root, AnalysisContext ctx, bool allowBareLiterals)
    {
        var slots = new List<NumberSlot>();
        if (root == null)
            return slots;

        var visited = new HashSet<DumpToken>();
        void Visit(DumpToken? t, bool insideArrayIndex)
        {
            if (t == null || !visited.Add(t))
                return;

            string? macro = ctx.GetMacroName(t);
            if (!string.IsNullOrEmpty(macro))
            {
                slots.AddRange(ExtractIdentifierNumbers(macro, t, insideArrayIndex));
                return;
            }

            if (t.Str == "[" && t.AstOperand1 != null && t.AstOperand2 != null)
            {
                Visit(t.AstOperand1, insideArrayIndex: false);
                Visit(t.AstOperand2, insideArrayIndex: true);
                return;
            }

            if (t.AstOperand1 == null && t.AstOperand2 == null)
            {
                if (t.IsIntLiteral)
                {
                    if ((insideArrayIndex || allowBareLiterals) && int.TryParse(t.Str, out int litNum))
                        slots.Add(new NumberSlot("[]#", litNum, insideArrayIndex, IsIdentifier: false, t.Str, t));
                }
                else if (IsIdentifierName(t.Str) && !IsExcludedTypeOrKeyword(t.Str))
                {
                    slots.AddRange(ExtractIdentifierNumbers(t.Str, t, insideArrayIndex));
                }
                return;
            }

            Visit(t.AstOperand1, insideArrayIndex);
            Visit(t.AstOperand2, insideArrayIndex);
        }

        Visit(root, insideArrayIndex: false);
        return slots;
    }

    // -------------------------------------------------------------------------
    // 규칙 3: consecutiveTargetOverwrite
    // -------------------------------------------------------------------------

    private sealed record PendingWrite(DumpToken EqToken, DumpToken LhsRoot, string TargetKey, string BaseVarKey, string DisplayName, bool IsLocalDirect);

    private static void AnalyzeConsecutiveOverwrites(List<Stmt> stmts, AnalysisContext ctx)
    {
        var pending = new Dictionary<string, PendingWrite>(StringComparer.Ordinal);
        bool prevWasSplittedZeroInit = false;

        foreach (var stmt in stmts)
        {
            if (stmt is BlockStmt block)
            {
                pending.Clear();
                AnalyzeConsecutiveOverwrites(block.Statements, ctx);
                prevWasSplittedZeroInit = false;
                continue;
            }

            if (stmt is IfStmt ifStmt)
            {
                pending.Clear();
                for (var cur = ifStmt; cur != null; cur = cur.ElseIf)
                {
                    AnalyzeConsecutiveOverwrites(cur.ThenStatements, ctx);
                    if (cur.ElseStatements.Count > 0)
                        AnalyzeConsecutiveOverwrites(cur.ElseStatements, ctx);
                }
                prevWasSplittedZeroInit = false;
                continue;
            }

            if (stmt is ForStmt forStmt)
            {
                pending.Clear();
                AnalyzeConsecutiveOverwrites(forStmt.BodyStatements, ctx);
                prevWasSplittedZeroInit = false;
                continue;
            }

            if (stmt is LoopStmt loopStmt)
            {
                pending.Clear();
                AnalyzeConsecutiveOverwrites(loopStmt.BodyStatements, ctx);
                prevWasSplittedZeroInit = false;
                continue;
            }

            if (stmt is SwitchStmt swStmt)
            {
                pending.Clear();
                foreach (var sec in swStmt.Sections)
                    AnalyzeConsecutiveOverwrites(sec.Statements, ctx);
                prevWasSplittedZeroInit = false;
                continue;
            }

            if (stmt is not SimpleStmt ss)
            {
                pending.Clear();
                prevWasSplittedZeroInit = false;
                continue;
            }

            if (ss.IsSplittedDecl)
            {
                prevWasSplittedZeroInit = true;
                continue;
            }

            if (ss.IsControlFlowBreak)
            {
                pending.Clear();
                prevWasSplittedZeroInit = false;
                continue;
            }

            bool wasSplitted = prevWasSplittedZeroInit;
            prevWasSplittedZeroInit = false;

            if (ss.AstRoot == null)
            {
                pending.Clear();
                continue;
            }

            var root = ss.AstRoot;
            if (root.Str == "=" && root.IsAssignmentOp && root.AstOperand1 != null && root.AstOperand2 != null)
            {
                var lhs = root.AstOperand1;
                var rhs = root.AstOperand2;

                // 1) 우변에서 참조되거나 함수 호출에 의해 읽힐 수 있는 대기 쓰기 해제
                InvalidateTouchedTargets(rhs, pending);

                // 방어적 초기화 'int x = 0;' 직후는 덮어쓰기 첫 기록에서 제외(단, 일반 대입은 기록)
                bool isDefensiveZeroDeclInit = wasSplitted && rhs.IsIntLiteral && rhs.Str == "0";

                if (!isDefensiveZeroDeclInit && TryBuildOverwriteTarget(lhs, out var targetKey, out var baseVarKey, out var displayName, out var isLocalDirect))
                {
                    if (pending.TryGetValue(targetKey, out var prevWrite))
                    {
                        ctx.Report(
                            RuleConsecutiveOverwrite,
                            root,
                            $"연속 대입 덮어쓰기: '{displayName}'에 대입한 값({prevWrite.EqToken.LineNr}행)을 한 번도 읽지 않고 같은 블록에서 즉시 새 값으로 덮어쓰고 있습니다.");
                    }

                    pending[targetKey] = new PendingWrite(root, lhs, targetKey, baseVarKey, displayName, isLocalDirect);
                }
                else
                {
                    // 복합 좌변(예: *p = ... 또는 arr[i] = ...)은 기저 변수 관련 대기 항목 무효화
                    InvalidateTouchedTargets(lhs, pending);
                }
            }
            else
            {
                InvalidateTouchedTargets(root, pending);
            }
        }
    }

    private static void InvalidateTouchedTargets(DumpToken expr, Dictionary<string, PendingWrite> pending)
    {
        if (pending.Count == 0)
            return;

        bool hasCall = false;
        var touchedVars = new HashSet<string>(StringComparer.Ordinal);
        WalkAst(expr, t =>
        {
            if (t.Str == "(" && !t.IsCast)
                hasCall = true;
            if (t.Type == "name" && !string.IsNullOrEmpty(t.Str))
                touchedVars.Add(GetVariableKey(t));
        });

        var toRemove = new List<string>();
        foreach (var kvp in pending)
        {
            var pw = kvp.Value;
            if (touchedVars.Contains(pw.BaseVarKey))
            {
                toRemove.Add(kvp.Key);
                continue;
            }
            // 전역·정적·포인터 역참조 대상은 함수 호출 시 간접 참조될 수 있으므로 해제
            if (hasCall && !pw.IsLocalDirect)
                toRemove.Add(kvp.Key);
        }

        foreach (var key in toRemove)
            pending.Remove(key);
    }

    private static bool TryBuildOverwriteTarget(
        DumpToken lhs,
        out string targetKey,
        out string baseVarKey,
        out string displayName,
        out bool isLocalDirect)
    {
        targetKey = string.Empty;
        baseVarKey = string.Empty;
        displayName = string.Empty;
        isLocalDirect = false;

        if (HasSideEffects(lhs) || IsVolatileTarget(lhs))
            return false;

        if (lhs.Type == "name" && IsIdentifierName(lhs.Str) && lhs.AstOperand1 == null && lhs.AstOperand2 == null)
        {
            targetKey = GetVariableKey(lhs);
            baseVarKey = targetKey;
            displayName = lhs.Str;
            isLocalDirect = lhs.Variable?.Access is "Local" or "Argument";
            return true;
        }

        if (lhs.Str is "." or "->" && lhs.AstOperand1 != null && lhs.AstOperand2 != null)
        {
            if (!TryBuildOverwriteTarget(lhs.AstOperand1, out var parentKey, out baseVarKey, out var parentDisp, out bool parentLocal))
                return false;

            targetKey = $"{parentKey}{lhs.Str}{lhs.AstOperand2.Str}";
            displayName = $"{parentDisp}{lhs.Str}{lhs.AstOperand2.Str}";
            isLocalDirect = parentLocal && lhs.Str == ".";
            return true;
        }

        if (lhs.Str == "[" && lhs.AstOperand1 != null && lhs.AstOperand2 != null)
        {
            if (!TryBuildOverwriteTarget(lhs.AstOperand1, out var arrKey, out baseVarKey, out var arrDisp, out bool arrLocal))
                return false;

            string? idxRepr = null;
            if (lhs.AstOperand2.IsIntLiteral)
                idxRepr = lhs.AstOperand2.Str;
            else if (lhs.AstOperand2.KnownIntValue.HasValue)
                idxRepr = lhs.AstOperand2.KnownIntValue.Value.ToString();

            if (idxRepr == null)
                return false;

            targetKey = $"{arrKey}[{idxRepr}]";
            displayName = $"{arrDisp}[{idxRepr}]";
            isLocalDirect = arrLocal && (lhs.AstOperand1.Variable?.IsPointer != true);
            return true;
        }

        return false;
    }

    private static bool IsVolatileTarget(DumpToken lhs)
    {
        bool isVolatile = false;
        WalkAst(lhs, t =>
        {
            if (t.ValueVolatileness > 0)
                isVolatile = true;
            if (t.Variable != null && (t.Variable.IsVolatile || t.Variable.Volatileness > 0))
                isVolatile = true;
            if (t.Str == "volatile")
                isVolatile = true;
        });
        return isVolatile;
    }

    // -------------------------------------------------------------------------
    // 규칙 4: conditionBodyMismatch
    // -------------------------------------------------------------------------

    private static void AnalyzeConditionBodyMismatches(List<Stmt> stmts, AnalysisContext ctx)
    {
        foreach (var stmt in stmts)
        {
            if (stmt is BlockStmt block)
            {
                AnalyzeConditionBodyMismatches(block.Statements, ctx);
                continue;
            }

            if (stmt is ForStmt forStmt)
            {
                AnalyzeConditionBodyMismatches(forStmt.BodyStatements, ctx);
                continue;
            }

            if (stmt is LoopStmt loopStmt)
            {
                AnalyzeConditionBodyMismatches(loopStmt.BodyStatements, ctx);
                continue;
            }

            if (stmt is SwitchStmt swStmt)
            {
                foreach (var sec in swStmt.Sections)
                    AnalyzeConditionBodyMismatches(sec.Statements, ctx);
                continue;
            }

            if (stmt is IfStmt ifStmt)
            {
                CheckIfChain(ifStmt, ctx);
            }
        }
    }

    private static void CheckIfChain(IfStmt head, AnalysisContext ctx)
    {
        var prevConditions = new List<(DumpToken CondRoot, HashSet<string> CondVars)>();
        bool anyModifiedSincePrev = false;

        for (var cur = head; cur != null; cur = cur.ElseIf)
        {
            // 하위 본문 재귀 검사
            AnalyzeConditionBodyMismatches(cur.ThenStatements, ctx);
            if (cur.ElseStatements.Count > 0)
                AnalyzeConditionBodyMismatches(cur.ElseStatements, ctx);

            if (cur.CondRoot == null)
                continue;

            // 1) NULL 가드 포인터와 본문 역참조 포인터 불일치
            CheckNullGuardBodyMismatch(cur, ctx);

            // 2) 범위 비교(&& / ||) 변수-상수 불일치 또는 동일 비교 중복
            CheckLogicalRangeMismatch(cur.CondRoot, ctx);

            // 3) if / else if 체인 중복 조건
            if (!HasSideEffects(cur.CondRoot))
            {
                var curVars = CollectVarKeys(cur.CondRoot);
                if (!ReferenceEquals(cur, head) && !anyModifiedSincePrev)
                {
                    foreach (var (prevRoot, _) in prevConditions)
                    {
                        if (AreAstEqual(prevRoot, cur.CondRoot, ctx))
                        {
                            ctx.Report(
                                RuleConditionBodyMismatch,
                                cur.FirstToken,
                                $"중복된 else if 조건식: 앞선 분기({prevRoot.LineNr}행)와 완전히 동일한 조건식이 수정 없이 반복되어 이 분기에 도달할 수 없습니다.");
                            break;
                        }
                    }
                }
                prevConditions.Add((cur.CondRoot, curVars));
            }

            // 앞선 then 본문에서 조건 변수가 수정되었으면 이후 중복 조건 비교 보수적 중단
            var modifiedInThen = CollectModifiedVarKeys(cur.ThenStatements);
            if (prevConditions.Any(p => p.CondVars.Overlaps(modifiedInThen)))
                anyModifiedSincePrev = true;
        }
    }

    private static void CheckNullGuardBodyMismatch(IfStmt ifStmt, AnalysisContext ctx)
    {
        if (ifStmt.ThenStatements.Count == 0 || ifStmt.CondRoot == null)
            return;

        if (!TryExtractNullGuardPointer(ifStmt.CondRoot, out var guardedPtr))
            return;

        // 본문에 중첩 분기나 함수 호출이 있으면 오탐 방지를 위해 건너뜀
        bool complexBody = false;
        bool guardedPtrUsed = false;
        var derefedPtrs = new List<DumpToken>();

        foreach (var s in ifStmt.ThenStatements)
        {
            if (s is not SimpleStmt ss || ss.AstRoot == null)
            {
                complexBody = true;
                break;
            }

            WalkAst(ss.AstRoot, t =>
            {
                if (t.Str == "(" && !t.IsCast)
                    complexBody = true;

                if (IsSameVariable(t, guardedPtr))
                    guardedPtrUsed = true;

                if ((t.Str is "->" or ".") && t.AstOperand1 != null && IsPointerToken(t.AstOperand1))
                    derefedPtrs.Add(t.AstOperand1);
                else if (t.Str == "[" && t.AstOperand1 != null && IsPointerToken(t.AstOperand1))
                    derefedPtrs.Add(t.AstOperand1);
                else if (t.Str == "*" && t.AstOperand2 == null && t.AstOperand1 != null && IsPointerToken(t.AstOperand1))
                    derefedPtrs.Add(t.AstOperand1);
            });

            if (complexBody)
                break;
        }

        if (complexBody || guardedPtrUsed || derefedPtrs.Count == 0)
            return;

        foreach (var otherPtr in derefedPtrs)
        {
            if (IsSameVariable(otherPtr, guardedPtr))
                continue;
            if (AreIdentifierNamesSimilar(guardedPtr.Str, otherPtr.Str))
            {
                ctx.Report(
                    RuleConditionBodyMismatch,
                    otherPtr,
                    $"조건식·본문 포인터 불일치: 조건문은 '{guardedPtr.Str}'의 NULL 여부를 검사했으나 본문에서는 '{guardedPtr.Str}'를 사용하지 않고 유사한 이름의 포인터 '{otherPtr.Str}'를 역참조하고 있습니다.");
                return;
            }
        }
    }

    private static bool TryExtractNullGuardPointer(DumpToken cond, out DumpToken ptrTok)
    {
        ptrTok = null!;

        if (IsPointerToken(cond))
        {
            ptrTok = cond;
            return true;
        }

        if (cond.Str == "!=" && cond.AstOperand1 != null && cond.AstOperand2 != null)
        {
            if (IsPointerToken(cond.AstOperand1) && IsNullConstantExpr(cond.AstOperand2))
            {
                ptrTok = cond.AstOperand1;
                return true;
            }
            if (IsPointerToken(cond.AstOperand2) && IsNullConstantExpr(cond.AstOperand1))
            {
                ptrTok = cond.AstOperand2;
                return true;
            }
        }

        return false;
    }

    private static bool IsPointerToken(DumpToken tok)
        => tok.Type == "name" && (tok.ValuePointer > 0 || tok.Variable?.IsPointer == true) && tok.Variable?.IsArray != true;

    private static bool IsNullConstantExpr(DumpToken tok)
    {
        if (tok.IsCast && tok.AstOperand1 != null)
            return IsNullConstantExpr(tok.AstOperand1);
        return (tok.IsIntLiteral && tok.Str == "0") || tok.Str == "NULL";
    }

    private static bool AreIdentifierNamesSimilar(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
            return false;
        if (string.Equals(NormalizeDigits(a), NormalizeDigits(b), StringComparison.Ordinal))
            return true;

        var partsA = a.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var partsB = b.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (partsA.Length > 1 || partsB.Length > 1)
        {
            foreach (var pa in partsA)
            {
                if (pa.Length >= 2 && partsB.Any(pb => string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }

        if (a.Length >= 3 && b.Length >= 3)
            return ComputeEditDistance(a, b) <= 2;

        return false;
    }

    private static int ComputeEditDistance(string a, string b)
    {
        var dp = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) dp[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int prev = dp[0];
            dp[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int temp = dp[j];
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[j] = Math.Min(Math.Min(dp[j] + 1, dp[j - 1] + 1), prev + cost);
                prev = temp;
            }
        }
        return dp[b.Length];
    }

    private readonly record struct ComparisonClause(DumpToken Root, DumpToken VarTok, string Op, DumpToken BoundTok, string BoundName);

    private static void CheckLogicalRangeMismatch(DumpToken condRoot, AnalysisContext ctx)
    {
        if (condRoot.Str is not ("&&" or "||"))
            return;

        string logicalOp = condRoot.Str;
        var terms = new List<DumpToken>();
        FlattenSameLogicalOp(condRoot, logicalOp, terms);
        if (terms.Count < 2)
            return;

        // 1) 동일 논리 체인 내 완전 동일한 순수 비교식 중복 (예: x >= 0 && y < MAX_Y && x >= 0)
        for (int i = 0; i < terms.Count; i++)
        {
            if (HasSideEffects(terms[i]))
                continue;
            for (int j = i + 1; j < terms.Count; j++)
            {
                if (HasSideEffects(terms[j]))
                    continue;
                if (AreAstEqual(terms[i], terms[j], ctx))
                {
                    ctx.Report(
                        RuleConditionBodyMismatch,
                        terms[j],
                        $"조건식 내 동일 비교 반복: 같은 '{logicalOp}' 조건식에 '{FormatSimpleExpr(terms[j], ctx)}' 비교가 중복 작성되어 있습니다.");
                    return;
                }
            }
        }

        // 2) 범위 비교에서 변수와 경계 매크로/상수 이름 불일치 (예: x >= MIN_X && x <= MAX_X && y >= MIN_Y && x <= MAX_Y)
        var clauses = new List<ComparisonClause>();
        foreach (var term in terms)
        {
            if (TryExtractComparisonClause(term, ctx, out var clause))
                clauses.Add(clause);
        }

        if (clauses.Count < 3)
            return;

        var distinctVars = clauses.Select(c => c.VarTok).GroupBy(GetVariableKey).Select(g => g.First()).ToList();
        if (distinctVars.Count < 2)
            return;

        foreach (var c in clauses)
        {
            if (string.IsNullOrEmpty(c.BoundName))
                continue;

            // 경계 상수 이름에 다른 변수(otherVar)의 구분 토큰이 들어있고, 현재 변수(c.VarTok)와는 맞지 않는지 확인
            foreach (var otherVar in distinctVars)
            {
                if (IsSameVariable(c.VarTok, otherVar))
                    continue;

                if (BoundNameMatchesVar(c.BoundName, otherVar.Str) &&
                    !BoundNameMatchesVar(c.BoundName, c.VarTok.Str))
                {
                    ctx.Report(
                        RuleConditionBodyMismatch,
                        c.Root,
                        $"범위 비교 변수 불일치: 경계 '{c.BoundName}'는 변수 '{otherVar.Str}'에 해당하지만 잘못된 변수 '{c.VarTok.Str}'와 비교하고 있습니다.");
                    return;
                }
            }
        }
    }

    private static void FlattenSameLogicalOp(DumpToken tok, string op, List<DumpToken> terms)
    {
        if (tok.Str == op && tok.AstOperand1 != null && tok.AstOperand2 != null)
        {
            FlattenSameLogicalOp(tok.AstOperand1, op, terms);
            FlattenSameLogicalOp(tok.AstOperand2, op, terms);
        }
        else
        {
            terms.Add(tok);
        }
    }

    private static bool TryExtractComparisonClause(DumpToken term, AnalysisContext ctx, out ComparisonClause clause)
    {
        clause = default;
        if (!ComparisonOps.Contains(term.Str) || term.AstOperand1 == null || term.AstOperand2 == null)
            return false;

        var op1 = term.AstOperand1;
        var op2 = term.AstOperand2;

        if (op1.Type == "name" && string.IsNullOrEmpty(ctx.GetMacroName(op1)) && !op1.IsIntLiteral)
        {
            string boundName = ctx.GetMacroName(op2) ?? (op2.Type == "name" ? op2.Str : string.Empty);
            clause = new ComparisonClause(term, op1, term.Str, op2, boundName);
            return true;
        }

        if (op2.Type == "name" && string.IsNullOrEmpty(ctx.GetMacroName(op2)) && !op2.IsIntLiteral)
        {
            string flippedOp = term.Str switch
            {
                "<" => ">",
                "<=" => ">=",
                ">" => "<",
                ">=" => "<=",
                _ => term.Str
            };
            string boundName = ctx.GetMacroName(op1) ?? (op1.Type == "name" ? op1.Str : string.Empty);
            clause = new ComparisonClause(term, op2, flippedOp, op1, boundName);
            return true;
        }

        return false;
    }

    private static bool BoundNameMatchesVar(string boundName, string varName)
    {
        if (string.IsNullOrEmpty(boundName) || string.IsNullOrEmpty(varName))
            return false;

        var boundParts = boundName.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var varParts = varName.Split('_', StringSplitOptions.RemoveEmptyEntries);

        // 예: boundName = MAX_Y, varName = y 또는 pos_y
        string lastVarPart = varParts[^1];
        if (boundParts.Any(bp => string.Equals(bp, lastVarPart, StringComparison.OrdinalIgnoreCase)))
            return true;

        // 예: boundName = CH2_MAX, varName = ch2
        if (boundParts.Any(bp => string.Equals(bp, varName, StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    private static string FormatSimpleExpr(DumpToken tok, AnalysisContext ctx)
    {
        if (tok.AstOperand1 != null && tok.AstOperand2 != null)
            return $"{FormatSimpleExpr(tok.AstOperand1, ctx)} {tok.Str} {FormatSimpleExpr(tok.AstOperand2, ctx)}";
        if (tok.AstOperand1 != null)
            return $"{tok.Str}{FormatSimpleExpr(tok.AstOperand1, ctx)}";
        return ctx.GetEffectiveSymbol(tok);
    }

    private static HashSet<string> CollectVarKeys(DumpToken root)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        WalkAst(root, t =>
        {
            if (t.Type == "name" && IsIdentifierName(t.Str) && !IsExcludedTypeOrKeyword(t.Str))
                keys.Add(GetVariableKey(t));
        });
        return keys;
    }

    private static HashSet<string> CollectModifiedVarKeys(List<Stmt> stmts)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        void CollectAssignedLhsVarKeys(DumpToken? lhs, HashSet<string> outKeys)
        {
            if (lhs == null) return;
            if (lhs.Type == "name")
            {
                outKeys.Add(GetVariableKey(lhs));
                return;
            }
            if (lhs.Str == "[")
            {
                // 배열 인덱스([idx])의 idx는 읽기이므로 기저 배열(AstOperand1)만 수정 대상으로 기록
                CollectAssignedLhsVarKeys(lhs.AstOperand1, outKeys);
                return;
            }
            if (lhs.Str is "." or "->")
            {
                CollectAssignedLhsVarKeys(lhs.AstOperand1, outKeys);
                CollectAssignedLhsVarKeys(lhs.AstOperand2, outKeys);
                return;
            }
            if (lhs.Str == "*" && lhs.AstOperand2 == null)
            {
                CollectAssignedLhsVarKeys(lhs.AstOperand1, outKeys);
            }
        }

        void CollectFromExpr(DumpToken? expr)
        {
            WalkAst(expr, t =>
            {
                if (t.IsAssignmentOp && t.AstOperand1 != null)
                {
                    CollectAssignedLhsVarKeys(t.AstOperand1, keys);
                }
                else if (t.Str is "++" or "--" && t.AstOperand1 != null)
                {
                    WalkAst(t.AstOperand1, opTok =>
                    {
                        if (opTok.Type == "name") keys.Add(GetVariableKey(opTok));
                    });
                }
                else if (t.Str == "&" && t.AstOperand2 == null && t.AstOperand1?.Type == "name")
                {
                    keys.Add(GetVariableKey(t.AstOperand1));
                }
            });
        }

        foreach (var s in stmts)
        {
            if (s is SimpleStmt ss) CollectFromExpr(ss.AstRoot);
            else if (s is BlockStmt b) keys.UnionWith(CollectModifiedVarKeys(b.Statements));
            else if (s is IfStmt iff)
            {
                for (var c = iff; c != null; c = c.ElseIf)
                {
                    keys.UnionWith(CollectModifiedVarKeys(c.ThenStatements));
                    keys.UnionWith(CollectModifiedVarKeys(c.ElseStatements));
                }
            }
            else if (s is ForStmt f) keys.UnionWith(CollectModifiedVarKeys(f.BodyStatements));
            else if (s is LoopStmt l) keys.UnionWith(CollectModifiedVarKeys(l.BodyStatements));
            else if (s is SwitchStmt sw)
            {
                foreach (var sec in sw.Sections)
                    keys.UnionWith(CollectModifiedVarKeys(sec.Statements));
            }
        }
        return keys;
    }

    // -------------------------------------------------------------------------
    // 규칙 5: loopIndexMismatch
    // -------------------------------------------------------------------------

    private static HashSet<string> CollectLoopVarKeys(IReadOnlyList<DumpToken> tokens)
    {
        var loopVars = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Str != "for" || i + 1 >= tokens.Count || tokens[i + 1].Str != "(")
                continue;

            int closeIdx = FindMatchingClose(tokens, i + 1, tokens.Count, "(", ")");
            if (closeIdx <= i + 1)
                continue;

            var openParen = tokens[i + 1];
            DumpToken? initRoot = null;
            if (openParen.AstOperand2 is { Str: ";" } firstSemi)
                initRoot = firstSemi.AstOperand1;
            else
                ExtractForPartsFromTokens(tokens, i + 2, closeIdx, out initRoot, out _, out _);

            if (TryGetForInitVar(initRoot, out var initVar))
                loopVars.Add(GetVariableKey(initVar));
        }
        return loopVars;
    }

    private static void AnalyzeLoopIndexMismatches(
        List<Stmt> stmts,
        HashSet<string> loopVarsInScope,
        AnalysisContext ctx)
    {
        foreach (var stmt in stmts)
        {
            if (stmt is ForStmt forStmt)
            {
                CheckForLoop(forStmt, loopVarsInScope, ctx);
                AnalyzeLoopIndexMismatches(forStmt.BodyStatements, loopVarsInScope, ctx);
            }
            else
            {
                RecurseSubStatements(stmt, sub => AnalyzeLoopIndexMismatches(sub, loopVarsInScope, ctx));
            }
        }
    }

    private static void CheckForLoop(ForStmt forStmt, HashSet<string> loopVarsInScope, AnalysisContext ctx)
    {
        if (!TryGetForInitVar(forStmt.InitRoot, out var initVar))
            return;

        string initKey = GetVariableKey(initVar);
        var condVars = CollectIntegerVars(forStmt.CondRoot);
        var incModVars = CollectIncrementedVars(forStmt.IncRoot);
        var bodyModVars = CollectModifiedVarKeys(forStmt.BodyStatements);

        // 1-a) init: v, inc: v, cond에는 v가 없고 다른 변수 u를 비교 (u가 본문에서 변경되지 않는 경우)
        if (forStmt.CondRoot != null &&
            incModVars.Any(t => string.Equals(GetVariableKey(t), initKey, StringComparison.Ordinal)) &&
            !condVars.Any(t => string.Equals(GetVariableKey(t), initKey, StringComparison.Ordinal)))
        {
            var otherCondVar = condVars.FirstOrDefault(t =>
                !string.Equals(GetVariableKey(t), initKey, StringComparison.Ordinal) &&
                !bodyModVars.Contains(GetVariableKey(t)) &&
                IsComparisonVar(forStmt.CondRoot, t));
            if (otherCondVar != null)
            {
                ctx.Report(
                    RuleLoopIndexMismatch,
                    forStmt.FirstToken,
                    $"for 루프 조건 변수 불일치: 루프 변수 '{initVar.Str}'를 초기화하고 증감하지만 종료 조건에서는 다른 변수 '{otherCondVar.Str}'를 검사하고 있습니다.");
                return;
            }
        }

        // 1-b) init: v, cond: v, inc는 v가 아닌 다른 변수 u만 증감하고 본문에서도 v를 변경하지 않음
        if (forStmt.IncRoot != null &&
            condVars.Any(t => string.Equals(GetVariableKey(t), initKey, StringComparison.Ordinal)) &&
            !incModVars.Any(t => string.Equals(GetVariableKey(t), initKey, StringComparison.Ordinal)) &&
            !bodyModVars.Contains(initKey))
        {
            var otherIncVar = incModVars.FirstOrDefault(t => !string.Equals(GetVariableKey(t), initKey, StringComparison.Ordinal));
            if (otherIncVar != null)
            {
                ctx.Report(
                    RuleLoopIndexMismatch,
                    forStmt.FirstToken,
                    $"for 루프 증감 변수 불일치: 루프 변수 '{initVar.Str}'를 초기화·검사하지만 증감식에서는 다른 변수 '{otherIncVar.Str}'를 변경하고 있습니다.");
                return;
            }
        }

        // 2) 헤더는 v(j)로 정상 구성되었으나 본문에서 v를 한 번도 읽지 않고 같은 함수의 다른 루프 변수 u(i)만 배열 인덱스로 사용
        bool headerUsesInitNormally =
            condVars.Any(t => string.Equals(GetVariableKey(t), initKey, StringComparison.Ordinal)) &&
            incModVars.Any(t => string.Equals(GetVariableKey(t), initKey, StringComparison.Ordinal));

        if (!headerUsesInitNormally || forStmt.BodyStatements.Count == 0)
            return;

        // 중첩 for 루프 자체의 루프 변수를 제외하고 본문에서 v 읽기 및 다른 루프 변수 [u] 인덱싱 검사
        bool initVarReadInBody = IsVarUsedInStatements(forStmt.BodyStatements, initKey);
        if (initVarReadInBody)
            return;

        var usedArrayIndexLoopVar = FindOtherLoopVarInArrayIndex(forStmt.BodyStatements, initKey, loopVarsInScope);
        if (usedArrayIndexLoopVar != null)
        {
            ctx.Report(
                RuleLoopIndexMismatch,
                usedArrayIndexLoopVar,
                $"for 루프 본문 인덱스 혼동: 현재 루프 변수 '{initVar.Str}'는 본문에서 전혀 사용하지 않고 다른 루프 변수 '{usedArrayIndexLoopVar.Str}'를 배열 인덱스로 사용하고 있습니다.");
        }
    }

    private static bool TryGetForInitVar(DumpToken? initRoot, out DumpToken varTok)
    {
        varTok = null!;
        if (initRoot == null || initRoot.Str != "=" || !initRoot.IsAssignmentOp || initRoot.AstOperand1 == null)
            return false;

        var lhs = initRoot.AstOperand1;
        if (lhs.Type != "name" || !IsIdentifierName(lhs.Str))
            return false;
        if (lhs.ValuePointer > 0 || lhs.Variable?.IsPointer == true)
            return false;
        if (lhs.ValueType is "float" or "double" or "long double")
            return false;

        varTok = lhs;
        return true;
    }

    private static List<DumpToken> CollectIntegerVars(DumpToken? root)
    {
        var list = new List<DumpToken>();
        WalkAst(root, t =>
        {
            if (t.Type == "name" && IsIdentifierName(t.Str) && !IsExcludedTypeOrKeyword(t.Str) &&
                t.ValuePointer == 0 && t.Variable?.IsPointer != true)
            {
                list.Add(t);
            }
        });
        return list;
    }

    private static List<DumpToken> CollectIncrementedVars(DumpToken? root)
    {
        var list = new List<DumpToken>();
        WalkAst(root, t =>
        {
            if (t.Str is "++" or "--" && t.AstOperand1?.Type == "name")
                list.Add(t.AstOperand1);
            else if (t.IsAssignmentOp && t.AstOperand1?.Type == "name")
                list.Add(t.AstOperand1);
        });
        return list;
    }

    private static bool IsComparisonVar(DumpToken condRoot, DumpToken varTok)
    {
        bool found = false;
        WalkAst(condRoot, t =>
        {
            if (ComparisonOps.Contains(t.Str) &&
                ((t.AstOperand1 != null && IsSameVariable(t.AstOperand1, varTok)) ||
                 (t.AstOperand2 != null && IsSameVariable(t.AstOperand2, varTok))))
            {
                found = true;
            }
        });
        return found;
    }

    private static bool IsVarUsedInStatements(List<Stmt> stmts, string varKey)
    {
        foreach (var s in stmts)
        {
            if (s is SimpleStmt ss)
            {
                bool found = false;
                WalkAst(ss.AstRoot, t =>
                {
                    if (t.Type == "name" && string.Equals(GetVariableKey(t), varKey, StringComparison.Ordinal))
                        found = true;
                });
                if (found) return true;
            }
            else if (s is BlockStmt b && IsVarUsedInStatements(b.Statements, varKey)) return true;
            else if (s is IfStmt iff)
            {
                for (var c = iff; c != null; c = c.ElseIf)
                {
                    bool inCond = false;
                    WalkAst(c.CondRoot, t =>
                    {
                        if (t.Type == "name" && string.Equals(GetVariableKey(t), varKey, StringComparison.Ordinal))
                            inCond = true;
                    });
                    if (inCond || IsVarUsedInStatements(c.ThenStatements, varKey) || IsVarUsedInStatements(c.ElseStatements, varKey))
                        return true;
                }
            }
            else if (s is ForStmt f)
            {
                bool inHeader = false;
                WalkAst(f.InitRoot, t => { if (t.Type == "name" && GetVariableKey(t) == varKey) inHeader = true; });
                WalkAst(f.CondRoot, t => { if (t.Type == "name" && GetVariableKey(t) == varKey) inHeader = true; });
                WalkAst(f.IncRoot, t => { if (t.Type == "name" && GetVariableKey(t) == varKey) inHeader = true; });
                if (inHeader || IsVarUsedInStatements(f.BodyStatements, varKey))
                    return true;
            }
            else if (s is LoopStmt l && IsVarUsedInStatements(l.BodyStatements, varKey)) return true;
            else if (s is SwitchStmt sw && sw.Sections.Any(sec => IsVarUsedInStatements(sec.Statements, varKey))) return true;
        }
        return false;
    }

    private static DumpToken? FindOtherLoopVarInArrayIndex(
        List<Stmt> stmts,
        string ownVarKey,
        HashSet<string> loopVarsInScope,
        HashSet<string>? activeNestedLoopVars = null)
    {
        activeNestedLoopVars ??= new HashSet<string>(StringComparer.Ordinal);

        DumpToken? CheckExpr(DumpToken? expr)
        {
            DumpToken? found = null;
            WalkAst(expr, t =>
            {
                if (found != null) return;
                if (t.Str == "[" && t.AstOperand2 != null)
                {
                    WalkAst(t.AstOperand2, idxTok =>
                    {
                        if (found != null || idxTok.Type != "name") return;
                        string key = GetVariableKey(idxTok);
                        if (key != ownVarKey &&
                            loopVarsInScope.Contains(key) &&
                            !activeNestedLoopVars.Contains(key))
                        {
                            found = idxTok;
                        }
                    });
                }
            });
            return found;
        }

        foreach (var s in stmts)
        {
            if (s is SimpleStmt ss)
            {
                var hit = CheckExpr(ss.AstRoot);
                if (hit != null) return hit;
            }
            else if (s is BlockStmt b)
            {
                var hit = FindOtherLoopVarInArrayIndex(b.Statements, ownVarKey, loopVarsInScope, activeNestedLoopVars);
                if (hit != null) return hit;
            }
            else if (s is IfStmt iff)
            {
                for (var c = iff; c != null; c = c.ElseIf)
                {
                    var hit = CheckExpr(c.CondRoot)
                        ?? FindOtherLoopVarInArrayIndex(c.ThenStatements, ownVarKey, loopVarsInScope, activeNestedLoopVars)
                        ?? FindOtherLoopVarInArrayIndex(c.ElseStatements, ownVarKey, loopVarsInScope, activeNestedLoopVars);
                    if (hit != null) return hit;
                }
            }
            else if (s is ForStmt innerFor)
            {
                var nextNested = new HashSet<string>(activeNestedLoopVars, StringComparer.Ordinal);
                if (TryGetForInitVar(innerFor.InitRoot, out var innerVar))
                    nextNested.Add(GetVariableKey(innerVar));
                var hit = FindOtherLoopVarInArrayIndex(innerFor.BodyStatements, ownVarKey, loopVarsInScope, nextNested);
                if (hit != null) return hit;
            }
            else if (s is LoopStmt l)
            {
                var hit = FindOtherLoopVarInArrayIndex(l.BodyStatements, ownVarKey, loopVarsInScope, activeNestedLoopVars);
                if (hit != null) return hit;
            }
            else if (s is SwitchStmt sw)
            {
                foreach (var sec in sw.Sections)
                {
                    var hit = FindOtherLoopVarInArrayIndex(sec.Statements, ownVarKey, loopVarsInScope, activeNestedLoopVars);
                    if (hit != null) return hit;
                }
            }
        }
        return null;
    }

    // -------------------------------------------------------------------------
    // 규칙 6: suspiciousCodeClone
    // -------------------------------------------------------------------------

    private static void AnalyzeSuspiciousClones(List<Stmt> stmts, AnalysisContext ctx)
    {
        // 1) 하위 블록 재귀 및 if/else·switch 분기 쌍 비교
        foreach (var stmt in stmts)
        {
            if (stmt is IfStmt ifStmt)
            {
                var branches = new List<List<SimpleStmt>>();
                for (var c = ifStmt; c != null; c = c.ElseIf)
                {
                    branches.Add(ExtractSimpleExecStmts(c.ThenStatements));
                    if (c.ElseStatements.Count > 0)
                        branches.Add(ExtractSimpleExecStmts(c.ElseStatements));
                    AnalyzeSuspiciousClones(c.ThenStatements, ctx);
                    if (c.ElseStatements.Count > 0)
                        AnalyzeSuspiciousClones(c.ElseStatements, ctx);
                }
                CompareBlockPairs(branches, ctx);
            }
            else if (stmt is SwitchStmt swStmt)
            {
                var cases = swStmt.Sections
                    .Select(sec => ExtractSimpleExecStmts(sec.Statements))
                    .Where(list => list.Count >= 3)
                    .ToList();
                CompareBlockPairs(cases, ctx);

                foreach (var sec in swStmt.Sections)
                    AnalyzeSuspiciousClones(sec.Statements, ctx);
            }
            else
            {
                RecurseSubStatements(stmt, sub => AnalyzeSuspiciousClones(sub, ctx));
            }
        }

        // 2) 인접 문장 묶음 클론 비교 (3문장 이상)
        var execStmts = ExtractSimpleExecStmts(stmts);
        if (execStmts.Count < 6)
            return;

        var usedIndices = new HashSet<int>();
        int maxLen = Math.Min(12, execStmts.Count / 2);
        for (int len = maxLen; len >= 3; len--)
        {
            for (int i = 0; i + (2 * len) <= execStmts.Count; i++)
            {
                if (Enumerable.Range(i, 2 * len).Any(idx => usedIndices.Contains(idx)))
                    continue;

                var b1 = execStmts.GetRange(i, len);
                var b2 = execStmts.GetRange(i + len, len);

                if (CompareCloneBlockPair(b1, b2, ctx))
                {
                    for (int k = i; k < i + (2 * len); k++)
                        usedIndices.Add(k);
                }
            }
        }
    }

    private static List<SimpleStmt> ExtractSimpleExecStmts(List<Stmt> stmts)
        => stmts.OfType<SimpleStmt>()
            .Where(s => !s.IsSplittedDecl && !s.IsControlFlowBreak && s.AstRoot != null)
            .ToList();

    private static void CompareBlockPairs(List<List<SimpleStmt>> blocks, AnalysisContext ctx)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].Count < 3)
                continue;
            for (int j = i + 1; j < blocks.Count; j++)
            {
                if (blocks[j].Count == blocks[i].Count)
                    CompareCloneBlockPair(blocks[i], blocks[j], ctx);
            }
        }
    }

    private static bool CompareCloneBlockPair(List<SimpleStmt> b1, List<SimpleStmt> b2, AnalysisContext ctx)
    {
        if (b1.Count < 3 || b1.Count != b2.Count)
            return false;

        if (!BlocksHaveMatchingSkeletons(b1, b2, ctx, normalizeCallNames: false))
            return false;

        // B1과 B2의 슬롯 식별자 대응 수집
        var pairsByB1Symbol = new Dictionary<string, List<(string B2Symbol, DumpToken B2Token)>>(StringComparer.Ordinal);

        for (int s = 0; s < b1.Count; s++)
        {
            var slots1 = new List<DumpToken>();
            var slots2 = new List<DumpToken>();
            BuildNormalizedSkeleton(b1[s].AstRoot, ctx, normalizeCallNames: false, slots1);
            BuildNormalizedSkeleton(b2[s].AstRoot, ctx, normalizeCallNames: false, slots2);
            if (slots1.Count != slots2.Count)
                return false;

            for (int k = 0; k < slots1.Count; k++)
            {
                string sym1 = ctx.GetEffectiveSymbol(slots1[k]);
                string sym2 = ctx.GetEffectiveSymbol(slots2[k]);
                if (IsExcludedTypeOrKeyword(sym1) || IsExcludedTypeOrKeyword(sym2))
                    continue;

                if (!pairsByB1Symbol.TryGetValue(sym1, out var list))
                {
                    list = new List<(string, DumpToken)>();
                    pairsByB1Symbol[sym1] = list;
                }
                list.Add((sym2, slots2[k]));
            }
        }

        bool reportedAny = false;
        foreach (var kvp in pairsByB1Symbol)
        {
            string symX = kvp.Key;
            var mapped = kvp.Value;
            if (mapped.Count < 3)
                continue;

            var unchanged = mapped.Where(m => string.Equals(m.B2Symbol, symX, StringComparison.Ordinal)).ToList();
            if (unchanged.Count != 1)
                continue;

            var majorityReplacement = mapped
                .Where(m => !string.Equals(m.B2Symbol, symX, StringComparison.Ordinal))
                .GroupBy(m => m.B2Symbol, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();

            if (majorityReplacement == null)
                continue;

            // X가 3회 이상 등장했고 과반수(>= 2회 && > 미치환 횟수)가 Y로 바뀌었으나 1곳만 X로 남은 경우
            if (majorityReplacement.Count() >= 2 && majorityReplacement.Count() + unchanged.Count == mapped.Count)
            {
                var missed = unchanged[0].B2Token;
                ctx.Report(
                    RuleSuspiciousClone,
                    missed,
                    $"유사 코드 클론 내 식별자 수정 누락 의심: 선행 블록의 '{symX}'가 이 블록에서는 대다수 '{majorityReplacement.Key}'(으)로 변경되었으나 이 위치만 '{symX}'로 남아 있습니다.");
                reportedAny = true;
            }
        }

        return reportedAny;
    }

    private static void RecurseSubStatements(Stmt stmt, Action<List<Stmt>> action)
    {
        switch (stmt)
        {
            case BlockStmt b:
                action(b.Statements);
                break;
            case IfStmt iff:
                for (var c = iff; c != null; c = c.ElseIf)
                {
                    action(c.ThenStatements);
                    if (c.ElseStatements.Count > 0)
                        action(c.ElseStatements);
                }
                break;
            case ForStmt f:
                action(f.BodyStatements);
                break;
            case LoopStmt l:
                action(l.BodyStatements);
                break;
            case SwitchStmt sw:
                foreach (var sec in sw.Sections)
                    action(sec.Statements);
                break;
        }
    }
}
