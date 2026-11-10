/* SPDX-License-Identifier: AGPL-3.0-only */
/* couchsubs_native.h arayüzünün libchiaki + FFmpeg üstündeki gerçeklemesi.
 * Davranış Mac sürümüyle (remoteplay/Sources/RemotePlay) aynıdır. */

#include "couchsubs_native.h"

#include <chiaki/common.h>
#include <chiaki/discovery.h>
#include <chiaki/log.h>
#include <chiaki/regist.h>
#include <chiaki/session.h>
#include <chiaki/thread.h>
#include <chiaki/time.h>

#include <libavcodec/avcodec.h>
#include <libavutil/frame.h>
#include <libavutil/channel_layout.h>
#include <libavutil/log.h>
#include <libavutil/mem.h>
#include <libswscale/swscale.h>

#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#ifndef SIO_UDP_CONNRESET
#define SIO_UDP_CONNRESET _WSAIOW(IOC_VENDOR, 12)
#endif
#else
#include <arpa/inet.h>
#include <netinet/in.h>
#include <pthread.h>
#include <time.h>
#endif

#define CS_LOG_MASK (CHIAKI_LOG_ERROR | CHIAKI_LOG_WARNING | CHIAKI_LOG_INFO)

static void cs_sleep_ms(unsigned int ms)
{
#ifdef _WIN32
	Sleep(ms);
#else
	struct timespec ts = { ms / 1000, (long)(ms % 1000) * 1000000L };
	nanosleep(&ts, NULL);
#endif
}

static int cs_level(ChiakiLogLevel level)
{
	if(level & CHIAKI_LOG_ERROR)
		return 0;
	if(level & CHIAKI_LOG_WARNING)
		return 1;
	return 2;
}

/* --- Günlük --- */

typedef struct {
	cs_log_cb cb;
	void *user;
} cs_log_target;

static void cs_log_forward(ChiakiLogLevel level, const char *message, void *user)
{
	cs_log_target *target = user;
	if(target && target->cb && message)
		target->cb(cs_level(level), message, target->user);
}

static void cs_logf(cs_log_cb cb, void *user, int level, const char *fmt, const char *arg)
{
	if(!cb)
		return;
	char line[256];
	snprintf(line, sizeof(line), fmt, arg);
	cb(level, line, user);
}

/* --- Başlatma --- */

static int cs_init_result = -1;

/* chiaki_lib_init WSAStartup'ı ve FEC'in Galois tablolarını hazırlar; bir kez yeterli. */
static void cs_init_once(void)
{
	/* FFmpeg günlüğü kapalı: uygulama penceresinde stderr yok, bozuk karelerde satır yağar. */
	av_log_set_level(AV_LOG_QUIET);
	cs_init_result = (int)chiaki_lib_init();
}

#ifdef _WIN32
static INIT_ONCE cs_once = INIT_ONCE_STATIC_INIT;

static BOOL CALLBACK cs_init_once_win(PINIT_ONCE once, PVOID parameter, PVOID *context)
{
	(void)once;
	(void)parameter;
	(void)context;
	cs_init_once();
	return TRUE;
}
#else
static pthread_once_t cs_once = PTHREAD_ONCE_INIT;
#endif

CS_API int cs_init(void)
{
#ifdef _WIN32
	InitOnceExecuteOnce(&cs_once, cs_init_once_win, NULL, NULL);
#else
	pthread_once(&cs_once, cs_init_once);
#endif
	return cs_init_result;
}

/* --- PS5 arama ve uyandırma --- */

typedef struct {
	cs_console_cb cb;
	void *user;
} cs_discover_ctx;

static void cs_discovery_cb(ChiakiDiscoveryHost *host, void *user)
{
	cs_discover_ctx *ctx = user;
	if(!host || !host->host_addr || !chiaki_discovery_host_is_ps5(host))
		return;
	ctx->cb(host->host_addr, host->host_name ? host->host_name : "PS5",
			host->state == CHIAKI_DISCOVERY_HOST_STATE_READY ? 1 : 0, ctx->user);
}

CS_API int cs_discover(const char *const *targets, int target_count, int timeout_ms, cs_console_cb callback, void *user)
{
	if(!targets || target_count <= 0 || !callback)
		return 1;

	/* Hedefsiz günlük: satırlar düşürülür (libchiaki'nin varsayılanı stdout'a yazardı). */
	ChiakiLog log;
	chiaki_log_init(&log, CHIAKI_LOG_ERROR, cs_log_forward, NULL);

	ChiakiDiscovery discovery;
	if(chiaki_discovery_init(&discovery, &log, AF_INET) != CHIAKI_ERR_SUCCESS)
		return -1;

#ifdef _WIN32
	/* Windows'ta ulaşılamayan bir adrese gönderilen paketin ICMP yanıtı recvfrom'u WSAECONNRESET ile
	 * bozar ve chiaki'nin arama iş parçacığı kapanır; tüm ağı taradığımız için bu kapatılmalı. */
	{
		BOOL report = FALSE;
		DWORD returned = 0;
		WSAIoctl(discovery.socket, SIO_UDP_CONNRESET, &report, sizeof(report), NULL, 0, &returned, NULL, NULL);
	}
#endif

	cs_discover_ctx ctx = { callback, user };
	ChiakiDiscoveryThread thread;
	if(chiaki_discovery_thread_start(&thread, &discovery, cs_discovery_cb, &ctx) != CHIAKI_ERR_SUCCESS)
	{
		chiaki_discovery_fini(&discovery);
		return -1;
	}

	struct sockaddr_in *addresses = calloc((size_t)target_count, sizeof(struct sockaddr_in));
	int address_count = 0;
	if(addresses)
	{
		for(int i = 0; i < target_count; i++)
		{
			if(!targets[i])
				continue;
			struct sockaddr_in *address = &addresses[address_count];
			address->sin_family = AF_INET;
			address->sin_port = htons(CHIAKI_DISCOVERY_PORT_PS5);
			if(inet_pton(AF_INET, targets[i], &address->sin_addr) == 1)
				address_count++;
		}
	}

	char version[] = CHIAKI_DISCOVERY_PROTOCOL_VERSION_PS5;
	ChiakiDiscoveryPacket packet;
	memset(&packet, 0, sizeof(packet));
	packet.cmd = CHIAKI_DISCOVERY_CMD_SRCH;
	packet.protocol_version = version;

	/* Yanıt kaçmasın diye arama süre dolana kadar tekrarlanır. */
	uint64_t end = chiaki_time_now_monotonic_ms() + (uint64_t)(timeout_ms > 0 ? timeout_ms : 0);
	do
	{
		for(int i = 0; i < address_count; i++)
			chiaki_discovery_send(&discovery, &packet, (struct sockaddr *)&addresses[i], sizeof(struct sockaddr_in));
		cs_sleep_ms(500);
	} while(chiaki_time_now_monotonic_ms() < end);

	chiaki_discovery_thread_stop(&thread);
	chiaki_discovery_fini(&discovery);
	int result = addresses ? 0 : -1;
	free(addresses);
	return result;
}

CS_API int cs_wake(const char *host, uint64_t credential)
{
	if(!host)
		return 1;
	ChiakiLog log;
	chiaki_log_init(&log, CHIAKI_LOG_ERROR, cs_log_forward, NULL);
	ChiakiErrorCode err = chiaki_discovery_wakeup(&log, NULL, host, credential, true);
	return err == CHIAKI_ERR_SUCCESS ? 0 : (int)err;
}

/* --- Kayıt --- */

static const char cs_reason_prefix[] = "Reported Application Reason: 0x";

/* "Reported Application Reason: 0x80108b02 (...)" satırından nedeni okur; yoksa false. */
static bool cs_reason_from_log(const char *line, uint32_t *reason)
{
	const char *found = line ? strstr(line, cs_reason_prefix) : NULL;
	if(!found)
		return false;
	const char *digits = found + sizeof(cs_reason_prefix) - 1;
	char *end = NULL;
	unsigned long value = strtoul(digits, &end, 16);
	if(end == digits)
		return false;
	*reason = (uint32_t)value;
	return true;
}

typedef struct {
	ChiakiMutex mutex;
	ChiakiCond cond;
	bool done;
	ChiakiRegistEventType type;
	bool has_host;
	ChiakiRegisteredHost host;
	uint32_t reason;
	cs_log_cb log;
	void *user;
} cs_regist_ctx;

static void cs_regist_log(ChiakiLogLevel level, const char *message, void *user)
{
	cs_regist_ctx *ctx = user;
	/* PS5'in red nedeni olaya değil yalnız günlüğe yazılır; olaydan önce aynı iş parçacığında gelir. */
	uint32_t reason;
	if(cs_reason_from_log(message, &reason))
	{
		chiaki_mutex_lock(&ctx->mutex);
		ctx->reason = reason;
		chiaki_mutex_unlock(&ctx->mutex);
	}
	if(ctx->log && message)
		ctx->log(cs_level(level), message, ctx->user);
}

static void cs_regist_event(ChiakiRegistEvent *event, void *user)
{
	cs_regist_ctx *ctx = user;
	chiaki_mutex_lock(&ctx->mutex);
	if(!ctx->done)
	{
		ctx->type = event ? event->type : CHIAKI_REGIST_EVENT_TYPE_FINISHED_FAILED;
		if(event && event->type == CHIAKI_REGIST_EVENT_TYPE_FINISHED_SUCCESS && event->registered_host)
		{
			ctx->host = *event->registered_host;
			ctx->has_host = true;
		}
		ctx->done = true;
	}
	chiaki_mutex_unlock(&ctx->mutex);
	chiaki_cond_signal(&ctx->cond);
}

static bool cs_regist_done(void *user)
{
	return ((cs_regist_ctx *)user)->done;
}

CS_API int cs_register(const char *host, const uint8_t account_id[8], uint32_t pin,
		cs_registered_console *out, uint32_t *reason, cs_log_cb log, void *user)
{
	if(reason)
		*reason = 0;
	if(!host || !account_id || !out)
		return -1;

	cs_regist_ctx *ctx = calloc(1, sizeof(cs_regist_ctx));
	if(!ctx)
		return -1;
	ctx->log = log;
	ctx->user = user;
	if(chiaki_mutex_init(&ctx->mutex, false) != CHIAKI_ERR_SUCCESS)
	{
		free(ctx);
		return -1;
	}
	if(chiaki_cond_init(&ctx->cond) != CHIAKI_ERR_SUCCESS)
	{
		chiaki_mutex_fini(&ctx->mutex);
		free(ctx);
		return -1;
	}

	ChiakiLog regist_log;
	chiaki_log_init(&regist_log, CS_LOG_MASK, cs_regist_log, ctx);

	ChiakiRegistInfo info;
	memset(&info, 0, sizeof(info));
	info.target = CHIAKI_TARGET_PS5_1;
	info.host = host;
	info.broadcast = false;
	info.psn_online_id = NULL;
	memcpy(info.psn_account_id, account_id, CHIAKI_PSN_ACCOUNT_ID_SIZE);
	info.pin = pin;
	info.console_pin = 0;
	info.holepunch_info = NULL;
	info.rudp = NULL;

	ChiakiRegist regist;
	int result;
	ChiakiErrorCode err = chiaki_regist_start(&regist, &regist_log, &info, cs_regist_event, ctx);
	if(err != CHIAKI_ERR_SUCCESS)
	{
		cs_logf(log, user, 0, "Regist start failed: %s", chiaki_error_string(err));
		result = -1;
	}
	else
	{
		chiaki_mutex_lock(&ctx->mutex);
		/* libchiaki'nin kendi zaman aşımları var; yine de yanıt gelmezse 30 sn sonra iptal edilir. */
		err = chiaki_cond_timedwait_pred(&ctx->cond, &ctx->mutex, 30000, cs_regist_done, ctx);
		if(err == CHIAKI_ERR_TIMEOUT)
		{
			chiaki_mutex_unlock(&ctx->mutex);
			chiaki_regist_stop(&regist);
			chiaki_mutex_lock(&ctx->mutex);
			chiaki_cond_wait_pred(&ctx->cond, &ctx->mutex, cs_regist_done, ctx);
		}
		chiaki_mutex_unlock(&ctx->mutex);
		/* Kayıt iş parçacığı geri çağrıdan sonra kapanır; bırakmadan önce beklenir. */
		chiaki_regist_fini(&regist);

		if(ctx->type == CHIAKI_REGIST_EVENT_TYPE_FINISHED_SUCCESS && ctx->has_host)
		{
			memset(out, 0, sizeof(*out));
			memcpy(out->nickname, ctx->host.server_nickname, sizeof(out->nickname));
			out->nickname[sizeof(out->nickname) - 1] = '\0';
			memcpy(out->regist_key, ctx->host.rp_regist_key, sizeof(out->regist_key));
			memcpy(out->morning, ctx->host.rp_key, sizeof(out->morning));
			result = 0;
		}
		else if(ctx->type == CHIAKI_REGIST_EVENT_TYPE_FINISHED_CANCELED)
			result = 2;
		else
		{
			if(reason)
				*reason = ctx->reason;
			result = 1;
		}
	}

	chiaki_cond_fini(&ctx->cond);
	chiaki_mutex_fini(&ctx->mutex);
	free(ctx);
	return result;
}

/* --- Kare sınırlayıcı --- */

/* Saniyede en fazla belirli sayıda karenin geçmesine izin verir (Mac'teki FrameThrottle). */
typedef struct {
	uint64_t min_interval_us;
	uint64_t last_us;
	bool has_last;
} cs_throttle;

static void cs_throttle_init(cs_throttle *throttle, int max_fps)
{
	throttle->min_interval_us = max_fps > 0 ? 1000000ULL / (uint64_t)max_fps : 0;
	throttle->last_us = 0;
	throttle->has_last = false;
}

static bool cs_throttle_should_pass(cs_throttle *throttle, uint64_t now_us)
{
	if(throttle->has_last && now_us - throttle->last_us < throttle->min_interval_us)
		return false;
	throttle->last_us = now_us;
	throttle->has_last = true;
	return true;
}

/* --- H.264 çözücü --- */

typedef struct {
	AVCodecContext *codec;
	AVPacket *packet;
	AVFrame *frame;
	struct SwsContext *sws;
	uint8_t *bgra;
	int width, height, stride;
	int source_format;
	int source_colorspace;
	int source_range;
} cs_decoder;

static int cs_decoder_init(cs_decoder *decoder)
{
	memset(decoder, 0, sizeof(*decoder));
	decoder->source_format = -1;
	const AVCodec *codec = avcodec_find_decoder(AV_CODEC_ID_H264);
	if(!codec)
		return -1;
	decoder->codec = avcodec_alloc_context3(codec);
	decoder->packet = av_packet_alloc();
	decoder->frame = av_frame_alloc();
	if(!decoder->codec || !decoder->packet || !decoder->frame)
		goto fail;
	/* Kare iş parçacıkları gecikme ekler; dilim iş parçacıkları eklemez. */
	decoder->codec->flags |= AV_CODEC_FLAG_LOW_DELAY;
	decoder->codec->thread_type = FF_THREAD_SLICE;
	decoder->codec->thread_count = 0;
	if(avcodec_open2(decoder->codec, codec, NULL) < 0)
		goto fail;
	return 0;
fail:
	avcodec_free_context(&decoder->codec);
	av_packet_free(&decoder->packet);
	av_frame_free(&decoder->frame);
	return -1;
}

static void cs_decoder_fini(cs_decoder *decoder)
{
	avcodec_free_context(&decoder->codec);
	av_packet_free(&decoder->packet);
	av_frame_free(&decoder->frame);
	sws_freeContext(decoder->sws);
	decoder->sws = NULL;
	av_freep(&decoder->bgra);
}

/* Son çözülen kareyi BGRA'ya çevirir. */
static bool cs_decoder_convert(cs_decoder *decoder, AVFrame *frame)
{
	int width = frame->width, height = frame->height;
	if(width <= 0 || height <= 0)
		return false;
	if(width != decoder->width || height != decoder->height || !decoder->bgra)
	{
		av_freep(&decoder->bgra);
		decoder->stride = FFALIGN(width * 4, 64);
		decoder->bgra = av_malloc((size_t)decoder->stride * (size_t)height);
		if(!decoder->bgra)
			return false;
		decoder->width = width;
		decoder->height = height;
	}
	bool changed = frame->format != decoder->source_format || frame->colorspace != decoder->source_colorspace
		|| frame->color_range != decoder->source_range;
	decoder->sws = sws_getCachedContext(decoder->sws, width, height, frame->format, width, height, AV_PIX_FMT_BGRA,
			SWS_POINT, NULL, NULL, NULL);
	if(!decoder->sws)
		return false;
	if(changed)
	{
		/* PS5 akışı BT.709; renk alanı belirtilmemişse HD için 709 varsayılır (swscale'in varsayılanı 601). */
		int space = frame->colorspace == AVCOL_SPC_BT470BG || frame->colorspace == AVCOL_SPC_SMPTE170M
			? SWS_CS_ITU601 : SWS_CS_ITU709;
		int full = frame->color_range == AVCOL_RANGE_JPEG || frame->format == AV_PIX_FMT_YUVJ420P;
		sws_setColorspaceDetails(decoder->sws, sws_getCoefficients(space), full, sws_getCoefficients(SWS_CS_ITU709), 1,
				0, 1 << 16, 1 << 16);
		decoder->source_format = frame->format;
		decoder->source_colorspace = frame->colorspace;
		decoder->source_range = frame->color_range;
	}
	uint8_t *dst[4] = { decoder->bgra, NULL, NULL, NULL };
	int dst_stride[4] = { decoder->stride, 0, 0, 0 };
	return sws_scale(decoder->sws, (const uint8_t *const *)frame->data, frame->linesize, 0, height, dst, dst_stride) == height;
}

/* Bir Annex-B örneğini çözer. Her örnek çözülmelidir (sonraki kareler öncekilere dayanır).
 * Dönüş: <0 çözücü hatası, 0 kare yok, 1 kare var (decoder->frame). */
static int cs_decoder_decode(cs_decoder *decoder, const uint8_t *data, size_t size)
{
	if(!data || !size)
		return 0;
	decoder->packet->data = (uint8_t *)data;
	decoder->packet->size = (int)size;
	int r = avcodec_send_packet(decoder->codec, decoder->packet);
	av_packet_unref(decoder->packet);
	if(r == AVERROR(EAGAIN))
	{
		/* Çözücü dolu: bekleyen kareler alınıp yeniden gönderilir. */
		while(avcodec_receive_frame(decoder->codec, decoder->frame) == 0)
			av_frame_unref(decoder->frame);
		decoder->packet->data = (uint8_t *)data;
		decoder->packet->size = (int)size;
		r = avcodec_send_packet(decoder->codec, decoder->packet);
		av_packet_unref(decoder->packet);
	}
	if(r < 0)
		return -1;
	int got = 0;
	AVFrame *next = av_frame_alloc();
	if(!next)
		return -1;
	/* Birden çok kare çıkarsa yalnız sonuncusu tutulur. */
	while(avcodec_receive_frame(decoder->codec, next) == 0)
	{
		av_frame_unref(decoder->frame);
		av_frame_move_ref(decoder->frame, next);
		got = 1;
	}
	av_frame_free(&next);
	return got;
}

/* --- Görüntü akışı --- */

/* --- Opus ses çözücü (FFmpeg'in kendi Opus çözücüsü) --- */

typedef struct {
	AVCodecContext *codec;
	AVPacket *packet;
	AVFrame *frame;
	float *mono;
	int mono_capacity;
	bool failed;
} cs_audio_decoder;

static void cs_audio_decoder_fini(cs_audio_decoder *decoder)
{
	avcodec_free_context(&decoder->codec);
	av_packet_free(&decoder->packet);
	av_frame_free(&decoder->frame);
	av_free(decoder->mono);
	decoder->mono = NULL;
	decoder->mono_capacity = 0;
}

static int cs_audio_decoder_init(cs_audio_decoder *decoder, int channels, int rate)
{
	cs_audio_decoder_fini(decoder);
	const AVCodec *codec = avcodec_find_decoder(AV_CODEC_ID_OPUS);
	if(!codec)
		return -1;
	decoder->codec = avcodec_alloc_context3(codec);
	decoder->packet = av_packet_alloc();
	decoder->frame = av_frame_alloc();
	if(!decoder->codec || !decoder->packet || !decoder->frame)
		goto fail;
	decoder->codec->sample_rate = rate > 0 ? rate : 48000;
	av_channel_layout_default(&decoder->codec->ch_layout, channels > 0 ? channels : 2);
	if(avcodec_open2(decoder->codec, codec, NULL) < 0)
		goto fail;
	return 0;
fail:
	cs_audio_decoder_fini(decoder);
	return -1;
}

struct cs_session {
	ChiakiSession session;
	ChiakiLog log;
	cs_log_target log_target;
	cs_frame_cb frame;
	cs_audio_cb audio;
	cs_event_cb event;
	void *user;
	cs_decoder decoder;
	cs_throttle throttle;
	bool decoder_ready;
	cs_audio_decoder audio_decoder;
};

static void cs_session_audio_header(ChiakiAudioHeader *header, void *user)
{
	cs_session *session = user;
	if(cs_audio_decoder_init(&session->audio_decoder, header->channels, (int)header->rate) != 0)
	{
		session->audio_decoder.failed = true;
		cs_logf(session->log_target.cb, session->user, 1, "%s", "Opus decoder could not be opened; game audio is off");
	}
	else
		session->audio_decoder.failed = false;
}

static void cs_session_audio_frame(uint8_t *buf, size_t buf_size, void *user)
{
	cs_session *session = user;
	cs_audio_decoder *decoder = &session->audio_decoder;
	if(!session->audio || decoder->failed || !decoder->codec || buf_size == 0)
		return;
	decoder->packet->data = buf;
	decoder->packet->size = (int)buf_size;
	if(avcodec_send_packet(decoder->codec, decoder->packet) < 0)
		return;
	while(avcodec_receive_frame(decoder->codec, decoder->frame) >= 0)
	{
		AVFrame *frame = decoder->frame;
		int count = frame->nb_samples;
		int channels = frame->ch_layout.nb_channels > 0 ? frame->ch_layout.nb_channels : 1;
		if(count > decoder->mono_capacity)
		{
			av_free(decoder->mono);
			decoder->mono = av_malloc_array((size_t)count, sizeof(float));
			decoder->mono_capacity = decoder->mono ? count : 0;
		}
		if(decoder->mono)
		{
			/* Kanallar ortalanır; FFmpeg'in Opus çözücüsü düzlemsel (planar) float verir. */
			for(int i = 0; i < count; i++)
			{
				float sum = 0.0f;
				for(int c = 0; c < channels; c++)
				{
					if(frame->format == AV_SAMPLE_FMT_FLTP)
						sum += ((const float *)frame->extended_data[c])[i];
					else if(frame->format == AV_SAMPLE_FMT_FLT)
						sum += ((const float *)frame->extended_data[0])[i * channels + c];
					else if(frame->format == AV_SAMPLE_FMT_S16)
						sum += ((const int16_t *)frame->extended_data[0])[i * channels + c] / 32768.0f;
				}
				decoder->mono[i] = sum / (float)channels;
			}
			session->audio(decoder->mono, count, frame->sample_rate, session->user);
		}
		av_frame_unref(frame);
	}
}

static int cs_quit_reason(ChiakiQuitReason reason)
{
	switch(reason)
	{
		case CHIAKI_QUIT_REASON_STOPPED:
			return CS_QUIT_STOPPED;
		case CHIAKI_QUIT_REASON_SESSION_REQUEST_RP_IN_USE:
			return CS_QUIT_IN_USE;
		case CHIAKI_QUIT_REASON_SESSION_REQUEST_CONNECTION_REFUSED:
		case CHIAKI_QUIT_REASON_CTRL_CONNECTION_REFUSED:
		case CHIAKI_QUIT_REASON_PSN_REGIST_FAILED:
			return CS_QUIT_REFUSED;
		case CHIAKI_QUIT_REASON_STREAM_CONNECTION_REMOTE_DISCONNECTED:
		case CHIAKI_QUIT_REASON_STREAM_CONNECTION_REMOTE_SHUTDOWN:
		case CHIAKI_QUIT_REASON_CTRL_CONNECT_FAILED:
		case CHIAKI_QUIT_REASON_STREAM_CONNECTION_UNKNOWN:
			return CS_QUIT_NETWORK;
		default:
			return CS_QUIT_OTHER;
	}
}

/* libchiaki'nin oturum iş parçacığında çağrılır. */
static void cs_session_event(ChiakiEvent *event, void *user)
{
	cs_session *session = user;
	if(!event || !session->event)
		return;
	switch(event->type)
	{
		case CHIAKI_EVENT_CONNECTED:
			session->event(CS_EVENT_CONNECTED, 0, NULL, session->user);
			break;
		case CHIAKI_EVENT_LOGIN_PIN_REQUEST:
			session->event(CS_EVENT_LOGIN_PIN, 0, NULL, session->user);
			break;
		case CHIAKI_EVENT_QUIT:
		{
			int reason = cs_quit_reason(event->quit.reason);
			/* Diğer nedenlerde açıklama libchiaki'nin neden adıdır (Mac'teki .other(String) gibi). */
			const char *text = reason == CS_QUIT_OTHER ? chiaki_quit_reason_string(event->quit.reason) : event->quit.reason_str;
			session->event(CS_EVENT_QUIT, reason, text, session->user);
			break;
		}
		default:
			break;
	}
}

/* Akış iş parçacığında çağrılır. Her örnek çözülür, görüntü en çok max_fps kez üretilir. */
static bool cs_session_video_sample(uint8_t *buf, size_t buf_size, int32_t frames_lost, bool frame_recovered, void *user)
{
	(void)frames_lost;
	(void)frame_recovered;
	cs_session *session = user;
	if(!session->decoder_ready)
		return true;
	int got = cs_decoder_decode(&session->decoder, buf, buf_size);
	if(got < 0)
		return false; /* libchiaki bozuk kare bildirip yeni anahtar kare ister */
	if(got == 0 || !session->frame)
		return true;
	if(!cs_throttle_should_pass(&session->throttle, chiaki_time_now_monotonic_us()))
		return true;
	cs_decoder *decoder = &session->decoder;
	if(cs_decoder_convert(decoder, decoder->frame))
		session->frame(decoder->bgra, decoder->width, decoder->height, decoder->stride, session->user);
	av_frame_unref(decoder->frame);
	return true;
}

CS_API cs_session *cs_session_start(const char *host, const uint8_t regist_key[0x10], const uint8_t morning[0x10],
		const uint8_t account_id[8], int max_fps,
		cs_frame_cb frame, cs_event_cb event, cs_log_cb log, void *user)
{
	return cs_session_start_with_audio(host, regist_key, morning, account_id, max_fps, frame, NULL, event, log, user);
}

CS_API cs_session *cs_session_start_with_audio(const char *host, const uint8_t regist_key[0x10], const uint8_t morning[0x10],
		const uint8_t account_id[8], int max_fps,
		cs_frame_cb frame, cs_audio_cb audio, cs_event_cb event, cs_log_cb log, void *user)
{
	if(!host || !regist_key || !morning || !account_id)
		return NULL;

	cs_session *session = calloc(1, sizeof(cs_session));
	if(!session)
		return NULL;
	session->frame = frame;
	session->audio = audio;
	session->event = event;
	session->user = user;
	session->log_target.cb = log;
	session->log_target.user = user;
	chiaki_log_init(&session->log, CS_LOG_MASK, cs_log_forward, &session->log_target);
	cs_throttle_init(&session->throttle, max_fps);

	if(cs_decoder_init(&session->decoder) != 0)
	{
		cs_logf(log, user, 0, "%s", "H.264 decoder could not be opened");
		free(session);
		return NULL;
	}
	session->decoder_ready = true;

	ChiakiConnectInfo info;
	memset(&info, 0, sizeof(info));
	info.ps5 = true;
	info.host = host; /* libchiaki adresi oturum açılırken kopyalar */
	memcpy(info.regist_key, regist_key, sizeof(info.regist_key));
	memcpy(info.morning, morning, sizeof(info.morning));
	chiaki_connect_video_profile_preset(&info.video_profile, CHIAKI_VIDEO_RESOLUTION_PRESET_1080p, CHIAKI_VIDEO_FPS_PRESET_30);
	info.video_profile.codec = CHIAKI_CODEC_H264;
	info.video_profile_auto_downgrade = true;
	info.enable_keyboard = false;
	info.enable_dualsense = false;
	/* Ses yalnız oyun sesi dinlenecekse istenir (0: ses ve görüntü açık). */
	info.audio_video_disabled = audio ? (ChiakiDisableAudioVideo)0 : CHIAKI_AUDIO_DISABLED;
	info.auto_regist = false;
	info.holepunch_session = NULL;
	info.rudp_sock = NULL;
	memcpy(info.psn_account_id, account_id, CHIAKI_PSN_ACCOUNT_ID_SIZE);
	info.packet_loss_max = 0.05;
	info.enable_idr_on_fec_failure = true;

	ChiakiErrorCode err = chiaki_session_init(&session->session, &info, &session->log);
	if(err != CHIAKI_ERR_SUCCESS)
	{
		cs_logf(log, user, 0, "Session init failed: %s", chiaki_error_string(err));
		cs_decoder_fini(&session->decoder);
		free(session);
		return NULL;
	}
	chiaki_session_set_event_cb(&session->session, cs_session_event, session);
	chiaki_session_set_video_sample_cb(&session->session, cs_session_video_sample, session);
	if(audio)
	{
		ChiakiAudioSink sink;
		sink.user = session;
		sink.header_cb = cs_session_audio_header;
		sink.frame_cb = cs_session_audio_frame;
		chiaki_session_set_audio_sink(&session->session, &sink);
	}

	err = chiaki_session_start(&session->session);
	if(err != CHIAKI_ERR_SUCCESS)
	{
		/* Başlamamış oturumun beklenecek iş parçacığı yok; yalnız bellek bırakılır. */
		cs_logf(log, user, 0, "Session start failed: %s", chiaki_error_string(err));
		chiaki_session_fini(&session->session);
		cs_decoder_fini(&session->decoder);
		free(session);
		return NULL;
	}
	return session;
}

CS_API void cs_session_stop(cs_session *session)
{
	if(!session)
		return;
	/* Durdurma, iş parçacıkları beklenene (join) kadar sürer; sonrasında hiçbir geri çağrı gelmez
	 * ve bellek güvenle bırakılabilir. Aynı oturum için yalnız bir kez çağrılmalı. */
	chiaki_session_stop(&session->session);
	chiaki_session_join(&session->session);
	chiaki_session_fini(&session->session);
	cs_decoder_fini(&session->decoder);
	cs_audio_decoder_fini(&session->audio_decoder);
	free(session);
}
