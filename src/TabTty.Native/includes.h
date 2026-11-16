#ifndef TABTTY_INCLUDES_H
#define TABTTY_INCLUDES_H

#include <stdint.h>
#include <stddef.h>
#include <stdlib.h>
#include <string.h>
#include <windows.h>

typedef uint8_t u_int8_t;
typedef uint16_t u_int16_t;
typedef uint32_t u_int32_t;

#define HAVE_STDLIB_H 1
#define HAVE_BLF_H 1

void tabtty_freezero(void *pointer, size_t length);
void tabtty_random(void *output, size_t length);
#define explicit_bzero(pointer, length) SecureZeroMemory((pointer), (length))
#define freezero(pointer, length) tabtty_freezero((pointer), (length))
#define arc4random_buf(pointer, length) tabtty_random((pointer), (length))

#endif
