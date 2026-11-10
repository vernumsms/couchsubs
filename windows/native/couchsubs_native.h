/* SPDX-License-Identifier: AGPL-3.0-only */
/* CouchSubs'ın Windows uygulaması için libchiaki üstünde düz C arayüzü (couchsubs_native.dll).
 * C# tarafı yalnız bu başlıktaki işlevleri P/Invoke ile çağırır; libchiaki yapılarına doğrudan dokunmaz.
 * Bütün dizgeler UTF-8, bütün işlevler iş parçacığı güvenlidir. Dönüş değeri 0 = başarı, diğerleri hata. */
#ifndef COUCHSUBS_NATIVE_H
#define COUCHSUBS_NATIVE_H

#include <stdint.h>

#ifdef _WIN32
#define CS_API __declspec(dllexport)
#else
#define CS_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* libchiaki günlük satırı. level: 0 hata, 1 uyarı, 2 bilgi. */
typedef void (*cs_log_cb)(int level, const char *message, void *user);

/* Ağ yığınını hazırlar (WSAStartup). Uygulama açılışında bir kez çağrılır. */
CS_API int cs_init(void);

/* --- PS5 arama ve uyandırma --- */

/* Bulunan her PS5 için çağrılır. awake: 1 açık, 0 dinlenme modunda. */
typedef void (*cs_console_cb)(const char *host, const char *name, int awake, void *user);

/* targets: sorulacak IPv4 adresleri (ör. "192.168.1.20", "255.255.255.255"). timeout_ms boyunca
 * aramayı 500 ms'de bir tekrarlar; aynı PS5 birden çok kez bildirilebilir. Bloklar. */
CS_API int cs_discover(const char *const *targets, int target_count, int timeout_ms, cs_console_cb callback, void *user);

/* Kayıtlı PS5'i dinlenme modundan uyandırır. credential: kayıt anahtarının onaltılık değeri. */
CS_API int cs_wake(const char *host, uint64_t credential);

/* --- Kayıt (PS5'in "Cihazı Eşleştir" kodu) --- */

typedef struct {
    char nickname[0x20];      /* sıfırla biten PS5 adı */
    uint8_t regist_key[0x10]; /* ASCII onaltılık, sıfırla doldurulmuş */
    uint8_t morning[0x10];
} cs_registered_console;

/* Bloklar (en çok ~30 sn). Başarısızlıkta *reason, PS5'in bildirdiği nedeni taşır
 * (ör. 0x80108b02 = hesap tanınmadı); bilinmiyorsa 0. Dönüş: 0 başarı, 1 başarısız, 2 iptal, <0 başlatılamadı. */
CS_API int cs_register(const char *host, const uint8_t account_id[8], uint32_t pin,
                       cs_registered_console *out, uint32_t *reason, cs_log_cb log, void *user);

/* --- Görüntü akışı --- */

/* Çözülmüş kare: BGRA, satır başına stride bayt. Bellek yalnız geri çağrı süresince geçerlidir. */
typedef void (*cs_frame_cb)(const uint8_t *bgra, int width, int height, int stride, void *user);

/* Oturum olayları. */
enum {
    CS_EVENT_CONNECTED = 0,
    CS_EVENT_QUIT = 1,            /* quit_reason: aşağıdaki CS_QUIT_* değerlerinden biri */
    CS_EVENT_LOGIN_PIN = 2        /* ikinci kullanıcının PS5 giriş şifresi var */
};
enum {
    CS_QUIT_STOPPED = 0,
    CS_QUIT_IN_USE = 1,           /* Remote Play başka cihazda kullanılıyor */
    CS_QUIT_REFUSED = 2,          /* bağlantı reddedildi / kayıt geçersiz */
    CS_QUIT_NETWORK = 3,          /* bağlantı koptu */
    CS_QUIT_OTHER = 4             /* reason_text açıklar */
};
typedef void (*cs_event_cb)(int event, int quit_reason, const char *reason_text, void *user);

/* Oyun sesi: tek kanala indirilmiş 32 bit kayan noktalı örnekler (rate Hz). Bellek yalnız geri çağrı süresince geçerlidir. */
typedef void (*cs_audio_cb)(const float *samples, int count, int rate, void *user);

typedef struct cs_session cs_session;

/* Ses ve kol girdisi kapalı, 1080p/30 H.264 akışı açar. Her kare çözülür; en çok max_fps kez
 * BGRA'ya çevrilip frame geri çağrısına verilir. Hata olursa NULL. Geri çağrılar oturumun kendi
 * iş parçacığında gelir. */
CS_API cs_session *cs_session_start(const char *host, const uint8_t regist_key[0x10], const uint8_t morning[0x10],
                                    const uint8_t account_id[8], int max_fps,
                                    cs_frame_cb frame, cs_event_cb event, cs_log_cb log, void *user);

/* cs_session_start gibi; audio NULL değilse oyunun sesi de istenir, Opus çözülüp tek kanal olarak audio'ya verilir
 * (oyun sesini dinleyip altyazısız konuşmaları çevirmek için). */
CS_API cs_session *cs_session_start_with_audio(const char *host, const uint8_t regist_key[0x10], const uint8_t morning[0x10],
                                    const uint8_t account_id[8], int max_fps,
                                    cs_frame_cb frame, cs_audio_cb audio, cs_event_cb event, cs_log_cb log, void *user);

/* Oturumu durdurur, iş parçacıklarını bekler ve belleği bırakır. Geri çağrı içinden çağrılmamalı. */
CS_API void cs_session_stop(cs_session *session);

#ifdef __cplusplus
}
#endif

#endif
