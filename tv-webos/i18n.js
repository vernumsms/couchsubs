// SPDX-License-Identifier: AGPL-3.0-only
// TV uygulamasının metinleri. Dil TV'nin diline göre seçilir; Türkçe değilse İngilizce.
(function () {
  'use strict';

  var TEXTS = {
    tr: {
      waiting: 'Bilgisayarınızda Altyazı+ uygulamasını açın.',
      waitingHint: 'Uygulama bu TV\'yi bulup altyazıları buraya gönderecek.',
      panelTitle: 'Altyazı+',
      hdmi: 'HDMI',
      lastSubtitle: 'Son altyazı',
      server: 'Uygulama',
      connection: 'Bağlantı',
      loading: '(yükleniyor)',
      videoReady: '(görüntü geldi)',
      playing: '(oynatılıyor)',
      inputError: '(hata: giriş açılamadı)',
      sourceError: '(hata: kaynak desteklenmiyor)',
      auto: 'otomatik',
      consoleNotFound: 'konsol bulunamadı',
      noEventSource: 'Bu TV desteklenmiyor (EventSource yok)',
      connecting: 'bağlanıyor…',
      connected: 'bağlı',
      reconnecting: 'bağlantı yok, yeniden deneniyor…',
      none: '—',
      keys: 'Tamam: bu panel · ◀▶: HDMI girişi (panel açıkken) · 1–4: HDMI girişi · 0: konsolu otomatik bul · ▲▼: altyazıyı yukarı/aşağı taşı · Kırmızı: test altyazısı · Mavi: demo · Yeşil: bu panel',
      localTest: 'Test: bu yazı oyunun üstünde görünüyor mu?',
      positionPreview: 'Altyazı burada görünecek',
      demoButton: 'Demoyu izle',
      demoBadge: 'DEMO',
      demoHint: 'Örnek: oyunun İngilizce altyazısı Türkçe olarak burada görünür. Çıkmak için Geri.'
    },
    en: {
      waiting: 'Open the Altyazı+ app on your computer.',
      waitingHint: 'The app will find this TV and send subtitles here.',
      panelTitle: 'Altyazı+',
      hdmi: 'HDMI',
      lastSubtitle: 'Last subtitle',
      server: 'App',
      connection: 'Connection',
      loading: '(loading)',
      videoReady: '(picture received)',
      playing: '(playing)',
      inputError: '(error: input could not be opened)',
      sourceError: '(error: source not supported)',
      auto: 'automatic',
      consoleNotFound: 'console not found',
      noEventSource: 'This TV is not supported (no EventSource)',
      connecting: 'connecting…',
      connected: 'connected',
      reconnecting: 'disconnected, retrying…',
      none: '—',
      keys: 'OK: this panel · ◀▶: HDMI input (panel open) · 1–4: HDMI input · 0: find console automatically · ▲▼: move subtitles up/down · Red: test subtitle · Blue: demo · Green: this panel',
      localTest: 'Test: is this text shown on top of the game?',
      positionPreview: 'Subtitles will appear here',
      demoButton: 'Watch the demo',
      demoBadge: 'DEMO',
      demoHint: 'Example: the game\'s English subtitles appear here in Turkish. Press Back to exit.'
    }
  };

  var lang = (navigator.language || 'en').toLowerCase().indexOf('tr') === 0 ? 'tr' : 'en';

  window.t = function (key) {
    return (TEXTS[lang] && TEXTS[lang][key]) || TEXTS.en[key] || key;
  };

  // data-i18n niteliği taşıyan öğelerin metnini doldurur.
  window.applyI18n = function () {
    var nodes = document.querySelectorAll('[data-i18n]');
    for (var i = 0; i < nodes.length; i++) {
      nodes[i].textContent = window.t(nodes[i].getAttribute('data-i18n'));
    }
  };
})();
