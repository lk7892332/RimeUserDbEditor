/*
 * 只 dump librime.so 的 rime_get_api() 回傳 struct。
 *
 * 不需要 librime header，也不在編譯時 link librime。程式只做三件事：
 *
 *   1. dlopen("librime.so.1.9.0")
 *   2. dlsym("rime_get_api") 並呼叫
 *   3. 依 librime ABI dump 回傳 struct 裡的 raw pointer slots
 *
 * Build:
 *
 *   cc -std=c11 -Wall -Wextra tools/rime_api_probe.c -ldl -o rime_api_probe
 *
 * Run:
 *
 *   ./rime_api_probe ./librime.so.1.9.0
 */

#include <dlfcn.h>
#include <inttypes.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

typedef struct rime_api_opaque_t RimeApi;
typedef RimeApi* (*rime_get_api_fn)(void);

_Static_assert(sizeof(rime_get_api_fn) == sizeof(void*),
               "this probe expects function pointers to fit in void*");

static size_t align_up(size_t value, size_t alignment) {
  return (value + alignment - 1) & ~(alignment - 1);
}

static size_t first_pointer_offset(void) {
  return align_up(sizeof(int), _Alignof(uintptr_t));
}

static size_t field_offset(size_t index) {
  return first_pointer_offset() + index * sizeof(uintptr_t);
}

static int read_data_size(const void* base) {
  int value = 0;
  memcpy(&value, base, sizeof(value));
  return value;
}

static size_t described_size(int data_size) {
  if (data_size < 0) {
    return 0;
  }
  return sizeof(int) + (size_t)data_size;
}

static size_t pointer_slot_count(int data_size) {
  size_t total = described_size(data_size);
  size_t first = first_pointer_offset();
  if (total <= first) {
    return 0;
  }
  return (total - first) / sizeof(uintptr_t);
}

static uintptr_t read_uintptr_at(const void* base, size_t offset) {
  uintptr_t value = 0;
  memcpy(&value, (const unsigned char*)base + offset, sizeof(value));
  return value;
}

static void dump_rime_api(const void* api) {
  int data_size = read_data_size(api);
  size_t slot_count = pointer_slot_count(data_size);

  printf("RimeApi @ %p\n", api);
  printf("  pointer_size      = %zu\n", sizeof(uintptr_t));
  printf("  first_ptr_offset  = %zu\n", first_pointer_offset());
  printf("  data_size         = %d\n", data_size);
  printf("  described_size    = %zu\n", described_size(data_size));
  printf("  pointer_slots     = %zu\n", slot_count);

  for (size_t i = 0; i < slot_count; ++i) {
    size_t offset = field_offset(i);
    uintptr_t value = read_uintptr_at(api, offset);
    printf("  slot[%-3zu] +%-3zu 0x%0*" PRIxPTR "%s\n",
           i,
           offset,
           (int)(sizeof(uintptr_t) * 2),
           value,
           value ? "" : " (null)");
  }
}

int main(int argc, char** argv) {
  const char* library_path = "librime.so.1.9.0";
  if (argc > 2) {
    fprintf(stderr, "Usage: %s [librime.so path]\n", argv[0]);
    return 2;
  }
  if (argc == 2) {
    library_path = argv[1];
  }

  void* handle = dlopen(library_path, RTLD_NOW | RTLD_LOCAL);
  if (!handle) {
    fprintf(stderr, "dlopen(%s) failed: %s\n", library_path, dlerror());
    return 1;
  }

  dlerror();
  void* symbol = dlsym(handle, "rime_get_api");
  const char* error = dlerror();
  if (error) {
    fprintf(stderr, "dlsym(rime_get_api) failed: %s\n", error);
    dlclose(handle);
    return 1;
  }

  rime_get_api_fn rime_get_api = NULL;
  memcpy(&rime_get_api, &symbol, sizeof(rime_get_api));

  RimeApi* api = rime_get_api();
  if (!api) {
    fprintf(stderr, "rime_get_api() returned null\n");
    dlclose(handle);
    return 1;
  }

  dump_rime_api(api);

  dlclose(handle);
  return 0;
}
