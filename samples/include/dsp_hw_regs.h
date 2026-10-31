#ifndef DSP_HW_REGS_H
#define DSP_HW_REGS_H

#include <stdint.h>
#include <stdbool.h>

#define PWM_MAX_DUTY        (32000)
#define ADC_CH_COUNT        (4)
#define CTRL_FLAG_READY     (0x01U)
#define CTRL_FLAG_FAULT     (0x80U)

typedef struct {
    int32_t target_rpm;
    int32_t current_rpm;
    int16_t pwm_output;
    uint16_t status_flags;
} MotorState_t;

int32_t hal_read_adc_raw(uint8_t channel);
int32_t hal_write_pwm_reg(int16_t duty_cycle);

#endif /* DSP_HW_REGS_H */
