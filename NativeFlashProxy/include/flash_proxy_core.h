#pragma once

#ifdef _WIN32
#define FLASH_PROXY_CALL __cdecl
#ifdef FLASH_PROXY_CORE_EXPORTS
#define FLASH_PROXY_API __declspec(dllexport)
#else
#define FLASH_PROXY_API __declspec(dllimport)
#endif
#else
#define FLASH_PROXY_CALL
#define FLASH_PROXY_API
#endif

extern "C" {

struct FlashProxyHandle;

FLASH_PROXY_API FlashProxyHandle* FLASH_PROXY_CALL flash_proxy_create();
FLASH_PROXY_API void FLASH_PROXY_CALL flash_proxy_destroy(FlashProxyHandle* handle);

FLASH_PROXY_API int FLASH_PROXY_CALL flash_proxy_set_cache_root(FlashProxyHandle* handle, const wchar_t* path);
FLASH_PROXY_API int FLASH_PROXY_CALL flash_proxy_clear_mapping_hosts(FlashProxyHandle* handle);
FLASH_PROXY_API int FLASH_PROXY_CALL flash_proxy_add_mapping_host(FlashProxyHandle* handle, const wchar_t* host);
FLASH_PROXY_API int FLASH_PROXY_CALL flash_proxy_clear_mapping_url_keywords(FlashProxyHandle* handle);
FLASH_PROXY_API int FLASH_PROXY_CALL flash_proxy_add_mapping_url_keyword(FlashProxyHandle* handle, const wchar_t* value);
FLASH_PROXY_API int FLASH_PROXY_CALL flash_proxy_set_upstream_proxy(FlashProxyHandle* handle, const wchar_t* proxy);

FLASH_PROXY_API int FLASH_PROXY_CALL flash_proxy_start(FlashProxyHandle* handle, int preferred_port, int* actual_port);
FLASH_PROXY_API void FLASH_PROXY_CALL flash_proxy_stop(FlashProxyHandle* handle);

FLASH_PROXY_API int FLASH_PROXY_CALL flash_proxy_get_last_error(FlashProxyHandle* handle, char* buffer, int buffer_size);
FLASH_PROXY_API void FLASH_PROXY_CALL flash_proxy_free_memory(void* ptr);

FLASH_PROXY_API int FLASH_PROXY_CALL flash_amf_encode_packet_json(const char* packet_json_utf8, unsigned char** out_data, int* out_size);
FLASH_PROXY_API int FLASH_PROXY_CALL flash_amf_decode_packet_json(const unsigned char* data, int data_size, char** out_json_utf8);
FLASH_PROXY_API int FLASH_PROXY_CALL flash_amf_post_json(const char* url_utf8, const char* packet_json_utf8, const char* headers_json_utf8, char** out_response_json_utf8);
FLASH_PROXY_API int FLASH_PROXY_CALL flash_amf_post_pvzol_json(const char* url_utf8, const char* target_utf8, const char* body_json_utf8, const char* cookie_utf8, const char* referer_utf8, const char* extra_headers_json_utf8, char** out_response_json_utf8);

}
