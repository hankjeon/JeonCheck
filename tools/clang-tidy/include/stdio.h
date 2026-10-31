#ifndef _STATICHECK_STDIO_H
#define _STATICHECK_STDIO_H

#include <stddef.h>

typedef struct _FILE FILE;
int printf(const char *format, ...);
int sprintf(char *str, const char *format, ...);
int snprintf(char *str, size_t size, const char *format, ...);
int scanf(const char *format, ...);
FILE *fopen(const char *pathname, const char *mode);
int fclose(FILE *stream);

#endif
