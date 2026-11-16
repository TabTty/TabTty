#include "includes.h"

typedef int (__cdecl *tabtty_hash_callback)(unsigned char *, const unsigned char *, size_t);
typedef int (__cdecl *tabtty_random_callback)(unsigned char *, size_t);

static __declspec(thread) tabtty_hash_callback hash_callback;
static __declspec(thread) tabtty_random_callback random_callback;
static __declspec(thread) int callback_failed;

int bcrypt_pbkdf(const char *, size_t, const uint8_t *, size_t, uint8_t *, size_t, unsigned int);

int tabtty_sha512(unsigned char *output, const unsigned char *input, unsigned long long length)
{
    if (length > 16384 || hash_callback(output, input, (size_t)length) != 1) {
        SecureZeroMemory(output, 64);
        callback_failed = 1;
        return -1;
    }
    return 0;
}

void tabtty_random(void *output, size_t length)
{
    if (random_callback(output, length) != 1) {
        SecureZeroMemory(output, length);
        callback_failed = 1;
    }
}

void tabtty_freezero(void *pointer, size_t length)
{
    if (pointer != NULL) {
        SecureZeroMemory(pointer, length);
        free(pointer);
    }
}

__declspec(dllexport) int __cdecl TermTabBcryptPbkdf(
    const unsigned char *password, size_t password_length,
    const unsigned char *salt, size_t salt_length,
    unsigned int rounds, unsigned char *key, size_t key_length,
    tabtty_hash_callback hash, tabtty_random_callback random)
{
    int status;
    if (password == NULL || password_length == 0 || password_length > 16384 ||
        salt == NULL || salt_length == 0 || salt_length > 64 ||
        key == NULL || key_length != 48 || rounds == 0 || rounds > 1024 ||
        hash == NULL || random == NULL)
        return -1;

    hash_callback = hash;
    random_callback = random;
    callback_failed = 0;
    status = bcrypt_pbkdf((const char *)password, password_length, salt, salt_length, key, key_length, rounds);
    if (status != 0 || callback_failed) {
        SecureZeroMemory(key, key_length);
        status = -1;
    }
    hash_callback = NULL;
    random_callback = NULL;
    callback_failed = 0;
    return status;
}
