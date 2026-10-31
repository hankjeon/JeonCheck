#include "dsp_hw_regs.h"

/* 비대상(참조 제외) 드라이버 파일 샘플 */
int32_t hal_read_adc_raw(uint8_t channel)
{
    volatile uint32_t *adc_base = (volatile uint32_t *)0x02320000U;
    return (int32_t)adc_base[channel];
}

int32_t hal_write_pwm_reg(int16_t duty_cycle)
{
    volatile int16_t *pwm_reg = (volatile int16_t *)0x02321000U;
    *pwm_reg = duty_cycle;
    return 0;
}
