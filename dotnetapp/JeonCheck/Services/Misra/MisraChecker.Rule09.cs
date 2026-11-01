namespace JeonCheck.Services.Misra;

// misra_9.py (규칙 9.2 ~ 9.5: 초기화식 구조 분석)
public sealed partial class MisraChecker
{
    /// <summary>배열·구조체·공용체 원소 정의 (misra_9.ElementDef)</summary>
    private sealed class ElementDef
    {
        public string ElementType;   // "array", "record", "value", "pointer"
        public string Name;
        public ValueType? ValueType;
        public List<ElementDef> Children = new();
        public ElementDef? Parent;
        public bool IsDesignated;
        public bool IsPositional;
        public int NumInits;
        public int ChildIndex = -1;
        public Token? FlexibleToken;
        public bool IsFlexible;
        public Token? StructureViolationToken;

        public ElementDef(string elementType, string name, ValueType? valueType)
        {
            ElementType = elementType;
            Name = name;
            ValueType = valueType;
        }

        /// <summary>list.index(x): 없으면 ValueError처럼 예외</summary>
        public static int IndexOrThrow(List<ElementDef> list, ElementDef? item)
        {
            int i = item == null ? -1 : list.IndexOf(item);
            if (i < 0) throw new InvalidOperationException("ValueError: list.index(x): x not in list");
            return i;
        }

        public bool IsArray => ElementType == "array";
        public bool IsRecord => ElementType == "record";
        public bool IsValue => ElementType == "value";

        public void AddChild(ElementDef child)
        {
            Children.Add(child);
            child.Parent = this;
        }

        public ElementDef? GetNextChild()
        {
            ChildIndex++;
            return GetChildByIndex(ChildIndex);
        }

        public ElementDef? GetChildByIndex(long index)
        {
            if (IsFlexible)
                while (Children.Count <= index)
                    CreateChild(this, FlexibleToken!, Children.Count.ToString(), null);
            return index >= 0 && index < Children.Count ? Children[(int)index] : null;
        }

        public ElementDef? GetChildByName(string name) => Children.FirstOrDefault(c => c.Name == name);

        public ElementDef? GetNextValueElement(ElementDef root)
        {
            var current = this;
            while (current != root)
            {
                if (current.Parent == null) return null;
                int i = IndexOrThrow(current.Parent.Children, current) + 1;
                if (i < current.Parent.Children.Count)
                {
                    current = current.Parent.Children[i];
                    return current.GetFirstValueElement();
                }
                current = current.Parent;
            }
            return null;
        }

        public ElementDef GetFirstValueElement()
        {
            var current = this;
            var next = current.GetChildByIndex(0);
            while (next != null)
            {
                current = next;
                next = current.GetChildByIndex(0);
            }
            return current;
        }

        public ElementDef GetLastValueElement()
        {
            var current = this;
            while (current.Children.Count > 0) current = current.Children[^1];
            return current;
        }

        public ElementDef? GetChildByValueElement(ElementDef? ed)
        {
            var potential = ed;
            while (potential != null && !Children.Contains(potential)) potential = potential.Parent;
            return potential != null ? Children[Children.IndexOf(potential)] : null;
        }

        public int GetEffectiveLevel() =>
            Parent != null && Parent.ElementType == "array" ? Parent.GetEffectiveLevel() + 1 : 0;

        public void SetInitialized(bool designated = false, bool positional = false)
        {
            if (designated) IsDesignated = true;
            if (positional || !designated) IsPositional = true;
            NumInits++;
        }

        public void InitializeChildren()
        {
            foreach (var child in Children)
            {
                child.SetInitialized(positional: true);
                child.InitializeChildren();
            }
        }

        public void Unset()
        {
            IsDesignated = false;
            IsPositional = false;
            foreach (var child in Children) child.Unset();
        }

        public void MarkStuctureViolation(Token token)
        {
            if (Name == "->") Children[0].MarkStuctureViolation(token);
            else if (StructureViolationToken == null) StructureViolationToken = token;
        }

        public void MarkAsFlexibleArray(Token token)
        {
            FlexibleToken = token;
            IsFlexible = true;
        }

        public void MarkAsCurrent()
        {
            if (Parent == null) return;
            Parent.ChildIndex = Name == "<-" ? IndexOrThrow(Parent.Children, Children[0]) : IndexOrThrow(Parent.Children, this);
            Parent.MarkAsCurrent();
        }

        public bool IsAllChildrenSet()
        {
            bool myself = Children.Count == 0 && (IsDesignated || IsPositional);
            bool mychildren = Children.Count > 0 && Children.Select(c => c.IsAllChildrenSet()).ToList().All(x => x);
            return myself || mychildren;
        }

        public bool IsAllSet() => Children.All(c => c.IsPositional || c.IsDesignated);
        public bool IsOnlyDesignated() => Children.All(c => !c.IsPositional);

        public bool IsMisra92Compliant() =>
            StructureViolationToken == null && Children.Select(c => c.IsMisra92Compliant()).ToList().All(x => x);

        public bool IsMisra93Compliant()
        {
            if (ElementType == "array")
            {
                return IsAllChildrenSet() ||
                       ((IsAllSet() || IsOnlyDesignated()) &&
                        Children.Select(c => !(c.IsDesignated || c.IsPositional) || c.IsMisra93Compliant()).ToList().All(x => x));
            }
            if (ElementType == "record")
                return Children.Select(c => c.IsMisra93Compliant()).ToList().All(x => x);
            return true;
        }

        public bool IsMisra94Compliant() =>
            NumInits <= 1 && Children.Select(c => c.IsMisra94Compliant()).ToList().All(x => x);

        public bool IsMisra95Compliant() => !IsFlexible || Children.All(c => !c.IsDesignated);
    }

    /// <summary>초기화식을 따라가며 ElementDef 상태를 갱신한다 (misra_9.InitializerParser)</summary>
    private sealed class InitializerParser
    {
        private Token? _token;
        private ElementDef? _root;
        private ElementDef? _ed;
        private readonly List<(ElementDef Old, ElementDef New)> _rootStack = new();

        public void ParseInitializer(ElementDef root, Token? token)
        {
            _token = token;
            var dummyRoot = new ElementDef("array", "->", root.ValueType);
            dummyRoot.Children = new List<ElementDef> { root };
            _rootStack.Clear();
            _root = dummyRoot;
            _ed = _root.GetFirstValueElement();
            bool isFirstElement = false;
            bool isDesignated = false;

            while (_token != null)
            {
                if (_token.Str == ",")
                {
                    _token = _token.AstOperand1;
                    isFirstElement = false;
                }
                else if (_token.IsAssignmentOp && _token.ValueType == null)
                {
                    // 지정 초기화 ( [2]=...  또는 .name=... )
                    PopFromStackIfExitElement();
                    _ed = GetElementByDesignator(_root, _token.AstOperand1!);
                    if (_ed != null)
                    {
                        PushToRootStackAndMarkAsDesignated();
                        _ed = _ed.GetFirstValueElement();
                    }
                    _token = _token.AstOperand2;
                    isFirstElement = false;
                    isDesignated = true;
                }
                else if (_token.IsString && _ed != null && _ed.IsArray)
                {
                    _ed.SetInitialized(isDesignated);
                    if (_token == _token.AstParent!.AstOperand1 && _token.AstParent.AstOperand2 != null)
                    {
                        _token = _token.AstParent.AstOperand2;
                        _ed.MarkAsCurrent();
                        _ed = _root!.GetNextChild();
                    }
                    else
                    {
                        UnwindAndContinue();
                    }
                    continue;
                }
                else if (_token.Str == "{")
                {
                    var nextChild = _root?.GetNextChild();
                    if (nextChild != null)
                    {
                        if (nextChild.IsArray || nextChild.IsRecord)
                        {
                            nextChild.Unset();
                            nextChild.SetInitialized(isDesignated);
                            _ed = nextChild.GetFirstValueElement();
                            isDesignated = false;
                        }
                        else if (nextChild.ValueType == null)
                        {
                            nextChild.SetInitialized(isDesignated);
                            UnwindAndContinue();
                            continue;
                        }
                        else if (_token.AstOperand1 != null)
                        {
                            // 초기화식의 초과 단계를 나타내는 가짜 상위 노드
                            var dummy = new ElementDef("array", "<-", _root!.ValueType)
                            {
                                Parent = _root,
                                ChildIndex = 0,
                            };
                            dummy.Children = new List<ElementDef> { nextChild };
                            nextChild.Parent = dummy;
                            _root.MarkStuctureViolation(_token);
                            nextChild = dummy;
                        }
                    }

                    if (nextChild != null && _token.AstOperand1 != null)
                    {
                        _root = nextChild;
                        _token = _token.AstOperand1;
                        isFirstElement = true;
                    }
                    else
                    {
                        if (_root != null)
                        {
                            if (_root.Name == "<-") _root.Parent!.MarkStuctureViolation(_token);
                            else _root.MarkStuctureViolation(_token);
                        }
                        _ed = null;
                        UnwindAndContinue();
                    }
                }
                else
                {
                    if (_ed != null && _ed.IsValue)
                    {
                        if (!isDesignated && _rootStack.Count > 0 && _rootStack[^1].New == _root)
                            _rootStack[^1].Old.MarkStuctureViolation(_token);

                        if (isFirstElement && _token.Str == "0" && _token.Next!.Str == "}")
                        {
                            _root!.InitializeChildren();
                        }
                        else if (_token.IsString && _ed.ValueType != null && _ed.ValueType.Pointer > 0)
                        {
                            if (_ed.ValueType.Pointer - _ed.GetEffectiveLevel() == 1)
                            {
                                if (_ed.Parent != _root) _root!.MarkStuctureViolation(_token);
                                _ed.SetInitialized(isDesignated);
                            }
                            else if (_ed.ValueType.Pointer == _ed.GetEffectiveLevel())
                            {
                                if ((_root!.Name != "->" && _ed.Parent!.Parent != _root) ||
                                    (_root.Name == "->" && _root.Children[0] != _ed.Parent))
                                    _root.MarkStuctureViolation(_token);
                                else
                                    _ed.Parent!.SetInitialized(isDesignated);
                                _ed.Parent!.InitializeChildren();
                            }
                        }
                        else
                        {
                            if (_root != null && _ed.Parent != _root)
                            {
                                var child = _root.GetChildByValueElement(_ed);
                                if (_token.ValueType != null)
                                {
                                    if (child!.ElementType != "record" || _token.ValueType.Type != "record" ||
                                        child.ValueType!.TypeScope != _token.ValueType.TypeScope)
                                        _root.MarkStuctureViolation(_token);
                                }
                            }
                            _ed.SetInitialized(isDesignated);
                        }

                        // 상위 원소까지 위치/지정 초기화 표시
                        var parent = _ed.Parent;
                        while (parent != null && parent != _root)
                        {
                            parent.IsDesignated = isDesignated && !parent.IsPositional ? isDesignated : parent.IsDesignated;
                            parent.IsPositional = !isDesignated && !parent.IsDesignated ? !isDesignated : parent.IsPositional;
                            parent = parent.Parent;
                        }
                        isDesignated = false;

                        if (_token.IsString && _ed.Parent!.IsArray) _ed = _ed.Parent;
                    }
                    UnwindAndContinue();
                }
            }
        }

        private void PushToRootStackAndMarkAsDesignated()
        {
            var @new = _ed!.Parent!;
            if (@new != _root)
            {
                var parent = @new;
                while (parent != null && parent != _root)
                {
                    parent.IsDesignated = true;
                    parent = parent.Parent;
                }
                _rootStack.Add((_root!, @new));
                @new.MarkAsCurrent();
            }
            @new.ChildIndex = ElementDef.IndexOrThrow(@new.Children, _ed) - 1;
            _root = @new;
        }

        private void PopFromStackIfExitElement()
        {
            if (_rootStack.Count > 0 && _rootStack[^1].New == _root)
            {
                var old = _rootStack[^1].Old;
                _rootStack.RemoveAt(_rootStack.Count - 1);
                old.MarkAsCurrent();
                _root = old;
            }
        }

        private void UnwindAndContinue()
        {
            while (_token != null)
            {
                if (_token.AstParent!.AstOperand1 == _token && _token.AstParent.AstOperand2 != null)
                {
                    if (_ed != null)
                    {
                        if (_token.AstParent.AstOperand2.Str == "{" && _ed.IsDesignated)
                        {
                            PopFromStackIfExitElement();
                        }
                        else
                        {
                            _ed.MarkAsCurrent();
                            _ed = _ed.GetNextValueElement(_root!);
                        }
                    }
                    _token = _token.AstParent.AstOperand2;
                    break;
                }

                _token = _token.AstParent;
                if (_token.Str == "{" && _root != null)
                {
                    _ed = _root.GetLastValueElement();
                    _ed.MarkAsCurrent();
                    if (_root.Name == "<-") _root.Children[0].Parent = _root.Parent;
                    _root = _root.Parent;
                }

                if (_token.AstParent == null)
                {
                    _token = null;
                    break;
                }
            }
        }
    }

    private void Misra_9_x(Configuration cfg, int rule, List<Token>? rawTokens = null)
    {
        var parser = new InitializerParser();
        foreach (var variable in cfg.Variables)
        {
            if (variable.NameToken == null) continue;
            var nameToken = variable.NameToken;
            // 선언과 초기화가 AST에서 두 문장으로 나뉜 경우
            if (nameToken.Next != null && nameToken.Next.IsSplittedVarDeclEq)
                nameToken = nameToken.Next.Next!;

            var eq = nameToken;
            while (!eq.IsAssignmentOp && eq.AstParent != null) eq = eq.AstParent;
            if (!eq.IsAssignmentOp || eq.AstOperand2!.IsName) continue;

            if (variable.IsArray || variable.IsClass)
            {
                var ed = GetElementDef(nameToken, rawTokens);
                if (ed.ValueType == null && !variable.IsArray) continue;
                parser.ParseInitializer(ed, eq.AstOperand2);
                if (rule == 902 && !ed.IsMisra92Compliant()) ReportError(nameToken, 9, 2);
                if (rule == 903 && !ed.IsMisra93Compliant())
                {
                    // 포인터 형 변수는 검사하지 않는다
                    var typeToken = variable.NameToken;
                    while (typeToken != null && typeToken.IsName) typeToken = typeToken.Previous;
                    if (typeToken != null && typeToken.Str == "*") continue;
                    ReportError(nameToken, 9, 3);
                }
                if (rule == 904 && !ed.IsMisra94Compliant()) ReportError(nameToken, 9, 4);
                if (rule == 905 && !ed.IsMisra95Compliant()) ReportError(nameToken, 9, 5);
            }
        }
    }

    /// <summary>str(Token)과 같이 실제 멤버 이름과 겹치지 않는 이름</summary>
    private static string TokenName(Token? t) => t == null ? "None" : $"Token(Id={t.Id})";

    private static ElementDef GetElementDef(Token nameToken, List<Token>? rawTokens = null)
    {
        ElementDef ed;
        if (nameToken.Variable!.IsArray)
        {
            ed = new ElementDef("array", nameToken.Str, nameToken.ValueType);
            CreateArrayChildrenDefs(ed, nameToken.AstParent, nameToken.Variable, rawTokens);
        }
        else if (nameToken.Variable.IsClass)
        {
            ed = new ElementDef("record", nameToken.Str, nameToken.ValueType);
            CreateRecordChildrenDefs(ed, nameToken.Variable);
        }
        else
        {
            ed = new ElementDef("value", nameToken.Str, nameToken.ValueType);
        }
        return ed;
    }

    private static void CreateArrayChildrenDefs(ElementDef ed, Token? token, Variable? var, List<Token>? rawTokens = null)
    {
        if (token == null || token.Str != "[") return;
        if (rawTokens != null)
        {
            var found = rawTokens.FirstOrDefault(r => r.File == token.File && r.Linenr == token.Linenr && r.Column == token.Column);
            if (found?.Next != null && found.Next.Str == "]") ed.MarkAsFlexibleArray(token);
        }
        long? size = token.AstOperand2?.GetKnownIntValue();
        if (token.AstOperand2 != null && size != null)
        {
            for (long i = 0; i < size.Value; i++)
                CreateChild(ed, token, i.ToString(), var);
        }
        else
        {
            ed.MarkAsFlexibleArray(token);
        }
    }

    private static void CreateChild(ElementDef ed, Token token, string name, Variable? var)
    {
        ElementDef child;
        if (token.AstParent != null && token.AstParent.Str == "[")
        {
            child = new ElementDef("array", name, ed.ValueType);
            CreateArrayChildrenDefs(child, token.AstParent, var);
        }
        else if (ed.ValueType != null && ed.ValueType.Type == "record")
        {
            child = new ElementDef("record", name, ed.ValueType);
            CreateRecordChildrenDefs(child, var);
        }
        else
        {
            child = new ElementDef("value", name, ed.ValueType);
        }
        ed.AddChild(child);
    }

    private static void CreateRecordChildrenDefs(ElementDef ed, Variable? var)
    {
        var valueType = ed.ValueType;
        if (valueType?.TypeScope == null) return;
        if (var == null) return;
        var typeToken = var.TypeEndToken;
        while (typeToken != null && typeToken.IsName) typeToken = typeToken.Previous;
        if (typeToken != null && typeToken.Str == "*")
        {
            ed.AddChild(new ElementDef("pointer", TokenName(var.NameToken), var.NameToken!.ValueType));
            return;
        }
        // 원본 dict: 키(토큰)별 마지막 값, 키 정렬은 (file, linenr, column)
        var childDict = new Dictionary<Token, ElementDef>(ReferenceEqualityComparer.Instance);
        var keys = new List<Token>();
        void Put(Token? key, ElementDef value)
        {
            if (key == null) throw new NullReferenceException("정렬할 원소 위치 토큰이 None입니다.");
            if (!childDict.ContainsKey(key)) keys.Add(key);
            childDict[key] = value;
        }
        foreach (var variable in valueType.TypeScope.Varlist)
        {
            if (ReferenceEquals(variable, var)) continue;
            Put(variable.NameToken, GetElementDef(variable.NameToken!));
        }
        foreach (var scopes in valueType.TypeScope.NestedList)
        {
            if (scopes.NestedIn != valueType.TypeScope) continue;
            bool varscope = valueType.TypeScope.Varlist.Any(v =>
                v.NameToken?.ValueType != null && v.NameToken.ValueType.TypeScope == scopes);
            if (varscope) continue;
            var ed1 = new ElementDef("record", Py.Str(scopes.Id), valueType);
            foreach (var variable in scopes.Varlist) ed1.AddChild(GetElementDef(variable.NameToken!));
            Put(scopes.BodyStart, ed1);
        }
        foreach (var key in keys.OrderBy(k => k.File, StringComparer.Ordinal).ThenBy(k => k.Linenr).ThenBy(k => k.Column))
            ed.AddChild(childDict[key]);
    }

    private static ElementDef? GetElementByDesignator(ElementDef? ed, Token token)
    {
        if (token.Str is not ("." or "[")) return null;
        while (token.Str is "." or "[") token = token.AstOperand1!;
        while (ed != null && !token.IsAssignmentOp)
        {
            token = token.AstParent!;
            if (token.Str == "[")
            {
                if (!ed.IsArray) ed.MarkStuctureViolation(token);
                long? chIndex = -1;
                if (token.AstOperand2 != null) chIndex = token.AstOperand2.GetKnownIntValue();
                else if (token.AstOperand1 != null) chIndex = token.AstOperand1.GetKnownIntValue();
                ed = chIndex != null ? ed.GetChildByIndex(chIndex.Value) : null;
            }
            else if (token.Str == ".")
            {
                if (!ed.IsRecord) ed.MarkStuctureViolation(token);
                string name = "";
                if (token.AstOperand2 != null) name = token.AstOperand2.Str;
                else if (token.AstOperand1 != null) name = token.AstOperand1.Str;
                ed = ed.GetChildByName(name);
            }
        }
        return ed;
    }
}
