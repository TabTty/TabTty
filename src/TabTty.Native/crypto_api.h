#ifndef TABTTY_CRYPTO_API_H
#define TABTTY_CRYPTO_API_H

#include "includes.h"

#define crypto_hash_sha512_BYTES 64
int tabtty_sha512(unsigned char *output, const unsigned char *input, unsigned long long length);
#define crypto_hash_sha512 tabtty_sha512

#endif
