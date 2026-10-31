#ifndef _STATICHECK_STDLIB_H
#define _STATICHECK_STDLIB_H

#include <stddef.h>

void *malloc(size_t size);
void *calloc(size_t nmemb, size_t size);
void *realloc(void *ptr, size_t size);
void free(void *ptr);
void abort(void);
void exit(int status);
int abs(int j);
long labs(long j);

#endif
