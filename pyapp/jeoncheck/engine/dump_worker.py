"""Cppcheck dump 해석 하위 프로세스: 줄별 검사 커버리지와 자체 검사 4종을 JSON으로 출력한다.

설치된 Cppcheck와 같은 버전의 addon(cppcheckdata.py)을 import하여 dump를 읽는다.
MISRA 규칙 자체는 misra.py를 별도로 실행하여 검사하며, 이 모듈은 misra.py에 없는 다음 검사만 수행한다.

- implicitNarrowing: 대입·반환·인자 전달 시 비트 폭이 줄어드는 암묵적 변환(정수 축소, double→float, 실수→정수)
- implicitSignConversion: 대입·반환·인자 전달 시 부호 있는/없는 정수 간 암묵적 변환
- explicitNarrowingCast: 더 작은 수치 타입으로의 명시적 캐스트
- pointerSizeMismatch: 포인터를 포인터보다 작은 정수형으로 캐스트

두 앱이 같은 결과를 내도록 필수 타입·상수 계산은 WPF 앱의 EssentialTypeResolver를,
검사 흐름은 TypeConversionAnalyzer를 그대로 옮겼다. sizeof(...) 값은 Cppcheck 값 흐름의 확정 값을 쓴다.

사용법: python3 dump_worker.py <dump 파일> --addon-dir <addon 폴더> --targets <대상 경로 JSON 파일> --base-dir <cppcheck 작업 폴더>
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import xml.etree.ElementTree as ElementTree
from collections import defaultdict

INTEGER_TYPES = ("char", "short", "int", "long", "long long")
FLOAT_TYPES = ("float", "double", "long double")
SKIP_CALL_NAMES = {"if", "for", "while", "switch", "sizeof", "return"}
LONG_MIN, LONG_MAX = -(1 << 63), (1 << 63) - 1


def normalize_path(path: str) -> str:
    # jeoncheck.models.normalize_path와 같은 규칙 (이 스크립트는 단독 실행되므로 복제)
    if not path or not path.strip():
        return ""
    return os.path.normcase(os.path.abspath(path)).replace("\\", "/")


class Platform:
    """dump의 <platform> 비트 폭 (cppcheckdata.Platform에 없는 실수형 비트 폭 포함)."""

    def __init__(self, dump_file: str):
        attrs = {}
        for _event, node in ElementTree.iterparse(dump_file, events=("start",)):
            if node.tag == "platform":
                attrs = dict(node.attrib)
                break
        get = lambda k, d: int(attrs.get(k, d))  # noqa: E731
        self.bits = {
            "bool": 1,
            "char": get("char_bit", 8),
            "short": get("short_bit", 16),
            "int": get("int_bit", 32),
            "long": get("long_bit", 32),
            "long long": get("long_long_bit", 64),
            "float": get("float_bit", 32),
            "double": get("double_bit", 64),
            "long double": get("long_double_bit", 64),
        }
        self.pointer_bit = get("pointer_bit", 32)

    def bit_width(self, vtype: str | None, pointer: int) -> int:
        if pointer > 0:
            return self.pointer_bit
        return self.bits.get(vtype or "", 0)


class EssentialType:
    """MISRA 필수 타입 범주(bool/char/signed/unsigned/float)와 비트 폭."""

    def __init__(self, category: str, bits: int, name: str):
        self.category = category
        self.bits = bits
        self.name = name

    @property
    def is_signed_or_unsigned(self) -> bool:
        return self.category in ("signed", "unsigned")

    @property
    def is_integer_like(self) -> bool:
        return self.is_signed_or_unsigned or self.category == "char"

    @property
    def display(self) -> str:
        return f"{self.name} ({self.bits}-bit)" if self.bits > 0 else self.name


class Checker:
    def __init__(self, platform: Platform, targets: set[str], base_dir: str):
        self.platform = platform
        self.targets = targets
        self.base_dir = base_dir
        self.issues: list[dict] = []
        self._emitted: set[tuple] = set()

    # ---- 경로 ----
    def abs_path(self, path: str) -> str:
        if not path:
            return ""
        return path if os.path.isabs(path) else os.path.normpath(os.path.join(self.base_dir, path))

    def in_targets(self, path: str) -> bool:
        return bool(path) and normalize_path(self.abs_path(path)) in self.targets

    def add(self, rule_id: str, tok, message: str) -> None:
        file = self.abs_path(tok.file)
        key = (rule_id, normalize_path(file), tok.linenr, tok.column)
        if key in self._emitted:
            return
        self._emitted.add(key)
        self.issues.append({"ruleId": rule_id, "file": file, "line": int(tok.linenr or 0),
                            "column": int(tok.column or 0), "message": message})

    # ---- 타입 ----
    @staticmethod
    def vt(tok):
        return getattr(tok, "valueType", None) if tok is not None else None

    # ---- WPF 앱 EssentialTypeResolver 이식 (두 앱의 자체 검사 결과를 같게 유지) ----
    @staticmethod
    def pointer_of(tok) -> int:
        v = Checker.vt(tok)
        return v.pointer if v else 0

    @staticmethod
    def vtype(tok):
        v = Checker.vt(tok)
        return v.type if v else None

    @staticmethod
    def vsign(tok):
        v = Checker.vt(tok)
        return v.sign if v else None

    @staticmethod
    def is_integer_value_type(tok) -> bool:
        return Checker.pointer_of(tok) == 0 and Checker.vtype(tok) in INTEGER_TYPES

    def resolve(self, expr) -> EssentialType | None:
        if expr is None or self.pointer_of(expr) > 0:
            return None
        s = expr.str
        # 부록 D.6: 문자 상수('a', '\n')는 문자형
        if self.is_char_literal(s):
            return EssentialType("char", self.platform.bits["char"], "char")
        # 부록 D.6: 정수 상수는 값을 표현할 수 있는 가장 작은 타입 (int 이외의 접미사 타입은 그대로)
        if getattr(expr, "isInt", False):
            if self.vtype(expr) == "int":
                ok, literal = self.try_eval_const_int(expr)
                if ok:
                    return self.from_value(literal, self.vsign(expr) != "unsigned") or self.from_value_type(expr)
            return self.from_value_type(expr)
        if getattr(expr, "isFloat", False):
            return self.from_value_type(expr)
        # 변수: 선언된 타입 (typedef 해제 후 'char' 토큰에 signed/unsigned가 없으면 일반 문자형)
        if expr.variable is not None:
            return self.from_value_type(expr, self.is_plain_char(expr.variable.typeStartToken))
        # 캐스트: 캐스트 대상 타입
        if getattr(expr, "isCast", False):
            return self.from_value_type(expr, self.is_plain_char_cast(expr.next))

        if s in ("<", "<=", ">", ">=", "==", "!=", "&&", "||", "!"):
            return EssentialType("bool", 1, "bool")
        if s in (",", "?"):
            return self.resolve(expr.astOperand2)  # '?'의 astOperand2는 ':' 토큰
        if s == "~" and expr.astOperand2 is None:
            return self.resolve(expr.astOperand1)
        if s in ("-", "+") and expr.astOperand2 is None and expr.astOperand1 is not None:
            if self.is_integer_value_type(expr):
                ok, unary = self.try_eval_const_int(expr)
                if ok:
                    return self.from_value(unary, self.vsign(expr) != "unsigned")
            return self.resolve(expr.astOperand1)

        # 이항 산술/비트/시프트 연산 및 조건 연산자의 ':'
        if expr.astOperand1 is not None and expr.astOperand2 is not None and \
                s in ("+", "-", "*", "/", "%", "&", "|", "^", "<<", ">>", ":"):
            if self.pointer_of(expr.astOperand1) > 0 or self.pointer_of(expr.astOperand2) > 0:
                return None
            e1 = self.resolve(expr.astOperand1)
            e2 = self.resolve(expr.astOperand2)
            if e1 is None or e2 is None:
                return None
            # 시프트 연산의 필수 타입은 좌변 피연산자의 필수 타입
            if s in ("<<", ">>"):
                if self.is_integer_value_type(expr) and e1.is_signed_or_unsigned:
                    ok, shift_val = self.try_eval_const_int(expr)
                    if ok:
                        by_shift = self.from_value(shift_val, e1.category == "signed")
                        if by_shift is not None:
                            return by_shift
                return e1
            # 양쪽 피연산자의 부호 범주가 같을 때만 정수 상수 수식을 값 기반 최소 타입으로 축소
            if s != ":" and e1.category == e2.category and e1.is_signed_or_unsigned and \
                    self.is_integer_value_type(expr):
                ok, const_value = self.try_eval_const_int(expr)
                if ok:
                    by_value = self.from_value(const_value, e1.category == "signed")
                    if by_value is not None:
                        return by_value
            if e1.bits != e2.bits:
                return e2 if e2.bits > e1.bits else e1
            # 비트 폭이 같고 한쪽이 Unsigned이면 C 표준 산술 변환과 동일하게 Unsigned 우선
            return e1 if e1.category == "unsigned" else e2

        # 함수 호출 결과(반환형), 배열 요소, 구조체 멤버, 포인터 역참조, 대입식
        is_call = s == "(" and expr.astOperand1 is not None and expr.astOperand1.type == "name"
        is_deref = s == "*" and expr.astOperand2 is None
        if is_call or is_deref or s in ("[", ".") or expr.isAssignmentOp:
            return self.from_value_type(expr, self.is_plain_char_expr(expr))
        # 열거형 상수 등 판정 불가 항목은 오탐 방지를 위해 None
        return None

    def from_value_type(self, tok, plain_char: bool = False) -> EssentialType | None:
        """토큰의 값 타입을 그대로 필수 타입으로 변환 (함수 반환형 등 승격이 없는 위치에 사용)."""
        vtype, sign = self.vtype(tok), self.vsign(tok)
        if self.pointer_of(tok) > 0 or not vtype:
            return None
        if vtype == "bool":
            return EssentialType("bool", 1, "bool")
        if vtype in FLOAT_TYPES:
            return EssentialType("float", self.platform.bit_width(vtype, 0), vtype)
        if vtype not in INTEGER_TYPES:
            return None
        bits = self.platform.bit_width(vtype, 0)
        if bits <= 0:
            return None
        if vtype == "char" and (plain_char or not sign):
            return EssentialType("char", bits, "char")
        signness = "unsigned" if sign == "unsigned" else "signed"
        return EssentialType(signness, bits, f"{signness} {vtype}")

    def from_value(self, value: int, is_signed: bool) -> EssentialType | None:
        """정수 상수 값을 표현할 수 있는 가장 작은 정수 타입."""
        if not is_signed and value < 0:
            return None
        for type_name in INTEGER_TYPES:
            bits = self.platform.bit_width(type_name, 0)
            if bits <= 0:
                continue
            if self.fits_in_bits(value, bits, not is_signed):
                signness = "signed" if is_signed else "unsigned"
                return EssentialType(signness, bits, f"{signness} {type_name}")
        return None

    @staticmethod
    def fits_in_bits(value: int, bits: int, unsigned: bool) -> bool:
        if bits >= 64:
            return not unsigned or value >= 0
        if unsigned:
            return 0 <= value <= (1 << bits) - 1
        return -(1 << (bits - 1)) <= value <= (1 << (bits - 1)) - 1

    @staticmethod
    def _checked(value: int) -> tuple[bool, int]:
        """C# checked long 연산과 같이 범위를 넘으면 계산 실패로 본다."""
        return (True, value) if LONG_MIN <= value <= LONG_MAX else (False, 0)

    def try_eval_const_int(self, tok) -> tuple[bool, int]:
        """정수 리터럴로만 이루어진 상수 수식의 값 (매크로 상수 확장 결과 포함). 계산 불가하면 (False, 0)."""
        if tok is None or self.pointer_of(tok) > 0:
            return False, 0
        # sizeof(...)는 Cppcheck가 대상 플랫폼 기준으로 계산한 값(값 흐름)을 쓴다
        if tok.str == "(" and tok.astOperand1 is not None and tok.astOperand1.str == "sizeof":
            size = tok.getKnownIntValue()
            return (True, size) if size is not None else (False, 0)
        if getattr(tok, "isInt", False):
            return self.try_parse_int_literal(tok.str)
        if getattr(tok, "isCast", False) or tok.variable is not None:
            return False, 0
        if tok.astOperand1 is not None and tok.astOperand2 is None:
            ok, v1 = self.try_eval_const_int(tok.astOperand1)
            if not ok:
                return False, 0
            if tok.str == "-":
                return self._checked(-v1)
            if tok.str == "+":
                return True, v1
            return False, 0
        if tok.astOperand1 is None or tok.astOperand2 is None:
            return False, 0
        ok, a = self.try_eval_const_int(tok.astOperand1)
        if not ok:
            return False, 0
        ok, b = self.try_eval_const_int(tok.astOperand2)
        if not ok:
            return False, 0
        op = tok.str
        if op == "+":
            return self._checked(a + b)
        if op == "-":
            return self._checked(a - b)
        if op == "*":
            return self._checked(a * b)
        if op in ("/", "%"):
            if b == 0:
                return False, 0
            q = abs(a) // abs(b) * (1 if (a >= 0) == (b >= 0) else -1)  # C#처럼 0 방향으로 자른다
            return self._checked(q) if op == "/" else self._checked(a - b * q)
        if op == "&":
            return True, a & b
        if op == "|":
            return True, a | b
        if op == "^":
            return True, a ^ b
        if op == "<<":
            if b < 0 or b > 62 or a < 0 or a > (LONG_MAX >> b):
                return False, 0
            return True, a << b
        if op == ">>":
            if b < 0 or b > 63:
                return False, 0
            return True, a >> b
        return False, 0

    @staticmethod
    def try_parse_int_literal(raw: str) -> tuple[bool, int]:
        if not raw or not raw.strip():
            return False, 0
        s = raw.strip().rstrip("uUlL")
        negative = s.startswith("-")
        if negative:
            s = s[1:]
        if s[:2].lower() == "0x":
            digits = s[2:]
            if not digits or any(c not in "0123456789abcdefABCDEF" for c in digits):
                return False, 0
            value = int(digits, 16)
        elif len(s) > 1 and s[0] == "0" and all(c in "01234567" for c in s):
            value = int(s, 8)
        else:
            if not s or any(c not in "0123456789" for c in s):
                return False, 0
            value = int(s)
        if value > LONG_MAX:
            return False, 0
        return True, -value if negative else value

    def constant_fits(self, tok, bits: int, unsigned: bool) -> bool:
        ok, value = self.try_eval_const_int(tok)
        return ok and self.fits_in_bits(value, bits, unsigned)

    def non_negative_constant(self, tok) -> bool:
        ok, value = self.try_eval_const_int(tok)
        return ok and value >= 0

    @staticmethod
    def is_char_literal(s: str) -> bool:
        return len(s) >= 3 and s[0] == "'" and s[-1] == "'" and (len(s) == 3 or (len(s) == 4 and s[1] == "\\"))

    @staticmethod
    def is_plain_char(type_tok) -> bool:
        """타입 선언 토큰열에서 signed/unsigned가 명시되지 않은 일반 'char'인지 확인."""
        t = type_tok
        while t is not None and t.type == "name":
            if t.str == "char":
                return not t.isSigned and not t.isUnsigned
            t = t.next
        return False

    @staticmethod
    def is_plain_char_cast(cast_tok) -> bool:
        t = cast_tok
        while t is not None and (t.type == "name" or t.str == "*"):
            if t.str == "char":
                return not t.isSigned and not t.isUnsigned
            t = t.next
        return False

    @staticmethod
    def is_plain_char_expr(expr) -> bool:
        """'[', 역참조 '*', '.', 대입식을 풀어 기저 변수의 선언 타입이 일반 'char'인지 확인."""
        vartok = expr
        while vartok is not None:
            if vartok.str == "[" or (vartok.str == "*" and vartok.astOperand2 is None):
                vartok = vartok.astOperand1
            elif vartok.str == ".":
                vartok = vartok.astOperand2
            elif vartok.isAssignmentOp:
                vartok = vartok.astOperand1
            else:
                break
        if vartok is not None and vartok.variable is not None:
            return Checker.is_plain_char(vartok.variable.typeStartToken)
        return False

    def resolve_parameter(self, param) -> EssentialType | None:
        """함수 매개변수의 필수 타입. 이름 없는 프로토타입 매개변수(예: f(int16_t))는 단일 타입 토큰으로 판정한다."""
        if param.nameToken is not None:
            return self.from_value_type(param.nameToken, self.is_plain_char(param.typeStartToken))
        type_tok = param.typeStartToken
        if type_tok is None or (param.typeStartTokenId or "") != (param.typeEndTokenId or ""):
            return None
        if type_tok.next is None or type_tok.next.str not in (",", ")"):
            return None  # 포인터('*') 등 추가 선언자가 있는 경우
        name = type_tok.str
        if name in ("bool", "_Bool"):
            return EssentialType("bool", 1, "bool")
        if name in ("float", "double"):
            return EssentialType("float", self.platform.bit_width(name, 0), name)
        if name == "char" and not type_tok.isSigned and not type_tok.isUnsigned:
            return EssentialType("char", self.platform.bits["char"], "char")
        if name in ("char", "short", "int"):
            signness = "unsigned" if type_tok.isUnsigned else "signed"
            return EssentialType(signness, self.platform.bit_width(name, 0), f"{signness} {name}")
        return None

    def type_display(self, tok) -> str:
        v = self.vt(tok)
        if v is None or not v.type:
            return "unknown"
        if v.pointer > 0:
            base = v.originalTypeName or v.type or "void"
            return f"{base}{'*' * v.pointer} ({self.platform.pointer_bit}-bit ptr)"
        bits = self.platform.bit_width(v.type, 0)
        name = f"{'unsigned ' if v.sign == 'unsigned' else ''}{v.type}".strip()
        return f"{name} ({bits}-bit)" if bits > 0 else name

    # ---- 검사 ----
    def check_implicit(self, target: EssentialType, source: EssentialType, source_expr, at_tok, msgs) -> None:
        """대입/반환/인자 전달 공통 암묵적 변환 검사 (WPF 앱 CheckImplicitConversion 1~4단계)."""
        narrow_int, narrow_float, float_to_int, sign_msg = msgs
        # 1) 정수 -> 더 작은 정수 (값이 대상 범위에 들어가는 정수 상수 수식은 제외)
        if target.is_integer_like and source.is_integer_like and source.bits > target.bits:
            if not self.constant_fits(source_expr, target.bits, target.category == "unsigned"):
                self.add("implicitNarrowing", at_tok, narrow_int(target, source))
        # 2) double -> float
        elif target.category == "float" and source.category == "float" and source.bits > target.bits:
            self.add("implicitNarrowing", at_tok, narrow_float(target, source))
        # 3) 실수 -> 정수
        elif target.is_integer_like and source.category == "float":
            self.add("implicitNarrowing", at_tok, float_to_int(target, source))
        # 4) 부호 변환 (음수가 아닌 상수를 부호 없는 타입에 대입하는 것은 허용)
        if target.is_signed_or_unsigned and source.is_signed_or_unsigned and target.category != source.category:
            if not (target.category == "unsigned" and self.non_negative_constant(source_expr)):
                self.add("implicitSignConversion", at_tok, sign_msg(target, source))

    def check_assignment(self, eq) -> None:
        lhs, rhs = eq.astOperand1, eq.astOperand2
        lt, rt = self.resolve(lhs), self.resolve(rhs)
        if lt is None or rt is None:
            return
        self.check_implicit(lt, rt, rhs, eq, (
            lambda l, r: f"암묵적 축소 대입: 필수 타입 '{r.display}' 수식('{rhs.str}')이 명시적 캐스트 없이 더 작은 '{l.display}' 변수('{lhs.str}')에 대입되었습니다.",
            lambda l, r: f"암묵적 실수 축소 대입: '{r.display}' 값('{rhs.str}')이 명시적 캐스트 없이 더 좁은 '{l.display}' 변수('{lhs.str}')에 대입되었습니다.",
            lambda l, r: f"부동소수점-정수 암묵적 변환: 실수형 '{r.display}' 수식이 명시적 캐스트 없이 정수형 '{l.display}' 변수('{lhs.str}')에 대입되었습니다 (소수점 이하 절삭).",
            lambda l, r: f"암묵적 부호 변환: 필수 타입 '{r.display}' 수식('{rhs.str}')이 부호가 다른 '{l.display}' 변수('{lhs.str}')에 명시적 캐스트 없이 대입되었습니다.",
        ))

    def check_return(self, ret) -> None:
        expr = ret.astOperand1
        # return 토큰의 값 타입은 함수의 선언된 반환형
        rt, et = self.from_value_type(ret), self.resolve(expr)
        if rt is None or et is None:
            return
        self.check_implicit(rt, et, expr, ret, (
            lambda l, r: f"함수 반환 시 암묵적 축소 변환: 필수 타입 '{r.display}' 수식이 더 작은 함수 반환형 '{l.display}'으로 캐스트 없이 반환되었습니다.",
            lambda l, r: f"함수 반환 시 암묵적 실수 축소 변환: '{r.display}' 수식이 더 좁은 함수 반환형 '{l.display}'으로 캐스트 없이 반환되었습니다.",
            lambda l, r: f"함수 반환 시 부동소수점-정수 암묵적 변환: 실수형 '{r.display}' 수식이 정수형 반환형 '{l.display}'으로 캐스트 없이 반환되었습니다.",
            lambda l, r: f"함수 반환 시 암묵적 부호 변환: 필수 타입 '{r.display}' 수식이 부호가 다른 반환형 '{l.display}'으로 캐스트 없이 반환되었습니다.",
        ))

    @staticmethod
    def flatten_args(root) -> list:
        result = []

        def walk(t):
            if t is None:
                return
            if t.str == ",":
                walk(t.astOperand1)
                walk(t.astOperand2)
            else:
                result.append(t)

        walk(root)
        return result

    def check_call_arguments(self, paren) -> None:
        func_tok = paren.astOperand1
        name = func_tok.str
        func = getattr(func_tok, "function", None)
        if name in SKIP_CALL_NAMES or func is None or paren.astOperand2 is None:
            return
        argument = getattr(func, "argument", None) or {}
        params = [argument[nr] for nr in sorted(argument)]  # 매개변수 번호 순서
        args = self.flatten_args(paren.astOperand2)
        # 가변 인자(...) 부분은 검사하지 않음
        for i, (arg, param) in enumerate(zip(args, params), start=1):
            if param is None:
                continue
            pt = self.resolve_parameter(param)
            at = self.resolve(arg)
            if pt is None or at is None:
                continue
            self.check_implicit(pt, at, arg, arg, (
                lambda l, r, n=i: f"함수 인자 전달 시 암묵적 축소 변환: '{name}()'의 {n}번째 인자(필수 타입 '{r.display}')가 명시적 캐스트 없이 더 작은 매개변수 타입 '{l.display}'으로 전달되었습니다.",
                lambda l, r, n=i: f"함수 인자 전달 시 암묵적 실수 축소 변환: '{name}()'의 {n}번째 인자('{r.display}')가 더 좁은 매개변수 타입 '{l.display}'으로 전달되었습니다.",
                lambda l, r, n=i: f"함수 인자 전달 시 부동소수점-정수 암묵적 변환: '{name}()'의 {n}번째 인자(실수형 '{r.display}')가 정수형 매개변수 '{l.display}'으로 전달되었습니다 (소수점 이하 절삭).",
                lambda l, r, n=i: f"함수 인자 전달 시 암묵적 부호 변환: '{name}()'의 {n}번째 인자(필수 타입 '{r.display}')가 부호가 다른 매개변수 타입 '{l.display}'으로 명시적 캐스트 없이 전달되었습니다.",
            ))

    def check_cast(self, cast) -> None:
        cv = self.vt(cast)
        if cv is None or (cv.type == "void" and cv.pointer == 0):
            return
        src = cast.astOperand1
        sv = self.vt(src)
        target_bits = self.platform.bit_width(cv.type, cv.pointer)
        src_bits = self.platform.bit_width(sv.type if sv else None, sv.pointer if sv else 0)
        target_ptr, src_ptr = cv.pointer > 0, (sv.pointer if sv else 0) > 0

        if target_ptr != src_ptr:
            if src_ptr and not target_ptr and 0 < target_bits < self.platform.pointer_bit:
                self.add("pointerSizeMismatch", cast,
                         f"포인터 주소 표현 범위 위험: {self.platform.pointer_bit}-bit 포인터를 더 작은 {target_bits}-bit 정수형으로 변환하고 있습니다. "
                         "주소를 대상 정수형으로 표현할 수 없으면 정의되지 않은 동작이 발생할 수 있습니다.")
            return
        if target_ptr and src_ptr:
            return  # 포인터 간 변환은 misra.py(11.x)가 검사

        src_ess = self.resolve(src)
        src_ess_bits = src_ess.bits if src_ess else src_bits
        if target_bits > 0 and src_ess_bits > target_bits and \
                not self.constant_fits(src, target_bits, cv.sign == "unsigned"):
            src_disp = src_ess.display if src_ess else self.type_display(src)
            self.add("explicitNarrowingCast", cast,
                     f"명시적 축소 캐스트: '{src_disp}'에서 더 작은 '{self.type_display(cast)}' 타입으로 형변환되었습니다.")

    def check_configuration(self, cfg) -> None:
        for tok in cfg.tokenlist:
            if not self.in_targets(tok.file):
                continue
            if tok.str == "=" and tok.isAssignmentOp and tok.astOperand1 and tok.astOperand2:
                self.check_assignment(tok)
            if tok.str == "return" and tok.astOperand1:
                self.check_return(tok)
            is_cast = getattr(tok, "isCast", False)
            if is_cast and tok.astOperand1:
                self.check_cast(tok)
            if tok.str == "(" and not is_cast and tok.astOperand1 is not None and tok.astOperand1.isName:
                self.check_call_arguments(tok)


def build_coverage(data, configurations, checker: Checker) -> dict:
    """tokenlist 줄 = 분석됨, rawtokens에만 있는 코드 줄 = 미검사, 지시문 줄은 중립 (WPF 앱 DumpFileParser와 같은 규칙)."""
    analyzed: dict[str, set] = defaultdict(set)
    directive: dict[str, set] = defaultdict(set)
    skipped: dict[str, set] = defaultdict(set)
    for cfg in configurations:
        for d in cfg.directives:
            if d.file and d.linenr and int(d.linenr) > 0:
                directive[checker.abs_path(d.file)].add(int(d.linenr))
        for t in cfg.tokenlist:
            if t.file and t.linenr and int(t.linenr) > 0:
                analyzed[checker.abs_path(t.file)].add(int(t.linenr))

    raw_by_line: dict[tuple, list] = defaultdict(list)
    for t in data.rawTokens:
        if not t.file or not t.linenr or not t.str or t.str.startswith(("/*", "//")):
            continue
        raw_by_line[(checker.abs_path(t.file), int(t.linenr))].append(t.str)
    for (file, line), toks in raw_by_line.items():
        if toks[0] == "#" or line in directive[file]:
            continue
        if line not in analyzed[file]:
            skipped[file].add(line)

    files = set(analyzed) | set(directive) | set(skipped)
    return {f: {"analyzed": sorted(analyzed[f]), "skipped": sorted(skipped[f]), "directive": sorted(directive[f])}
            for f in files}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("dump")
    parser.add_argument("--addon-dir", required=True)
    parser.add_argument("--targets", required=True, help="검사 대상 파일 경로 목록 JSON 파일")
    parser.add_argument("--base-dir", default=os.getcwd(), help="dump 안 상대 경로의 기준 폴더(Cppcheck 작업 폴더)")
    args = parser.parse_args(argv)

    sys.path.insert(0, args.addon_dir)
    import cppcheckdata  # noqa: E402  (설치된 Cppcheck addon)

    with open(args.targets, encoding="utf-8") as f:
        targets = {normalize_path(p) for p in json.load(f)}

    data = cppcheckdata.CppcheckData(args.dump)
    platform = Platform(args.dump)
    checker = Checker(platform, targets, args.base_dir)
    configurations = list(data.iterconfigurations())
    for cfg in configurations:
        checker.check_configuration(cfg)

    json.dump({"coverage": build_coverage(data, configurations, checker), "issues": checker.issues},
              sys.stdout, ensure_ascii=False)
    return 0


if __name__ == "__main__":
    sys.exit(main())
