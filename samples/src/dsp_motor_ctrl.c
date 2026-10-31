#include "dsp_hw_regs.h"
#include "missing_ti_csl_header.h" /* 의도적 누락 헤더: Info/Note 탭 분리 확인용 */

/* TI DSP 전용 키워드 테스트 (프리셋에서 자동 치환되어 구문 오류 없이 파싱됨) */
cregister volatile unsigned int CSR;

/* [Clang-Tidy 검출 대상] bugprone-macro-parentheses: 매크로 인자에 괄호 누락 */
#define SCALE_TORQUE(val)   val * 4

static int32_t s_adc_buffer[ADC_CH_COUNT];

/* 1. 암묵적 축소 변환 (Implicit Narrowing) 및 부호 변환 테스트 함수 */
int16_t calc_motor_pwm(int32_t pid_output, double filter_gain)
{
    /* [검출 대상] 32-bit(int32_t) -> 16-bit(int16_t) 암묵적 축소 대입 */
    int16_t duty_cmd = pid_output;

    /* [검출 대상] 64-bit(double) -> 32-bit(float) 암묵적 축소 대입 */
    float gain_f32 = filter_gain;

    /* [검출 대상] signed(int32_t) -> unsigned(uint32_t) 암묵적 부호 변환 */
    uint32_t unsigned_cmd = pid_output;

    /* [검출 대상] MISRA 15.6: if 문 중괄호 { } 누락 */
    if (duty_cmd > PWM_MAX_DUTY)
        duty_cmd = PWM_MAX_DUTY;

#ifdef ENABLE_EXPERIMENTAL_LUT
    /* 이 블록은 매크로가 정의되지 않았을 때 코드 뷰어에서 [미검사(Skipped) 줄]로 표시됨 */
    int32_t lut_offset = 128;
    duty_cmd = (int16_t)(duty_cmd + lut_offset);
#endif

    /* [검출 대상] MISRA 17.7: 반환값이 있는 함수의 결과값 무시 */
    hal_write_pwm_reg(duty_cmd);

    (void)gain_f32;
    (void)unsigned_cmd;

    /* [검출 대상] 함수 반환 시 int32_t 수식을 int16_t 반환형으로 암묵적 축소 반환 */
    return pid_output + 10;
}

/* 2. 배열 범위 초과, 제어문(switch/for) 및 포인터 규칙 테스트 */
interrupt void motor_control_isr(MotorState_t *p_state, uint32_t mode)
{
    int32_t uninit_acc; /* [검출 대상] 초기화되지 않은 변수 */
    int32_t ch;

    /* [검출 대상] 배열 범위 초과 (ADC_CH_COUNT는 4인데 ch <= 4 까지 접근) */
    for (ch = 0; ch <= ADC_CH_COUNT; ch++) {
        s_adc_buffer[ch] = hal_read_adc_raw((uint8_t)ch);
    }

    /* [검출 대상] MISRA 14.1: 실수형(float) 루프 카운터 사용 */
    for (float step = 0.0f; step < 1.0f; step += 0.25f) {
        p_state->target_rpm += 1;
    }

    /* [검출 대상] MISRA 16.4 (default 누락) 및 MISRA 16.3 (break 누락) */
    switch (mode) {
        case 1U:
            p_state->status_flags = CTRL_FLAG_READY;
            /* 의도적 break 누락 (Fall-through) */
        case 2U:
            p_state->status_flags = CTRL_FLAG_FAULT;
            break;
    }

    /* [검출 대상] 초기화되지 않은 지역 변수 읽기 */
    if (uninit_acc > 100) {
        p_state->pwm_output = 0;
    }

    /* [Clang-Tidy 검출 대상] bugprone-branch-clone: if와 else 분기에 실수로 동일한 코드 작성 */
    if (mode == 0U) {
        p_state->target_rpm = SCALE_TORQUE(100);
    } else {
        p_state->target_rpm = SCALE_TORQUE(100);
    }
}
