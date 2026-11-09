CouchSubs'ın **Claude yapay zekâ çevirili** sürümü (vernumsms fork'u). Orijinal proje: [yorulmazsinan/couchsubs](https://github.com/yorulmazsinan/couchsubs).

**İndir:** [Windows (kurulum)](https://github.com/vernumsms/couchsubs/releases/latest/download/CouchSubs-windows-Setup.exe)

**Bu sürümde yeni**

- **Yapay zekâ çevirisi (Claude).** Bilgisayardaki çeviri modeli her satırı hemen gösterir; bir an sonra Claude onu deyimleri, argoyu ve bağlamı anlayan doğal Türkçeyle değiştirir ("We're gonna call his bluff" → "Blöfünü göreceğiz"). Önceki satırları ve konuşmacı adlarını bağlam olarak kullanır; seçenek listelerini satır satır çevirir; yazı tanıma hatalarını ("cali" → "call") düzeltir. Ayarlar › Yapay zekâ çevirisi'nden kendi Anthropic anahtarınızla açılır; Claude Opus 5.5 (kalite) ya da Claude Haiku 5.5 (hız, düşük maliyet) seçilebilir. Claude'a ulaşılamazsa bilgisayardaki çeviri kalır.
- **Anlık altyazı.** Yalnız oyunda o an duran satırın çevirisi gösterilir: yeni satır gelince eskisi hemen kalkar, geç gelen eski çeviriler gösterilmez. Hızlı sahnelerde altyazılar birbirine karışmaz. Varsayılan olarak açık; Ayarlar › Anlık altyazı'dan kapatılabilir.
- Uygulama içi güncelleme bu fork'un sürümlerini izler.

---

CouchSubs'ın yeni deneme sürümü. Türkçe dil desteği olmayan PS5 oyunlarının İngilizce altyazısını okur, Türkçesini TV'de oyunun üstünde gösterir ve isterseniz Türkçe seslendirir.

**İndir:** [Mac (DMG)](https://github.com/yorulmazsinan/couchsubs/releases/latest/download/CouchSubs-macos.dmg) · [Windows (kurulum)](https://github.com/yorulmazsinan/couchsubs/releases/latest/download/CouchSubs-windows-Setup.exe)

**Bu sürümde yeni**

- **LG TV'de "TV uygulaması kurulamadı" hatası düzeltildi.** CouchSubs kurulumu TV'de bitmeden bağlantıyı kapatıyordu; bazı LG'lerde kurulum yarıda kalıyor ve hata yanlışlıkla boş alanı gösteriyordu. Artık LG'nin kendi aracı gibi kurulumun bitmesini bekliyor; başarısız olursa TV'nin bildirdiği sebebi gösteriyor (bildiren: [yag_ats](https://x.com/yag_ats)).

**beta.19'da gelenler:** Samsung ve Android TV'de görünmeyen altyazının düzeltilmesi (TV uygulaması 0.9.6), Windows Güvenlik Duvarı izni, GTA V ve renkli zeminlerde daha düzgün çeviri (Windows), ortalı altyazının kutusunun ortada durması (Mac ve Windows).

**beta.18'de gelenler:** OBS katmanı (Mac ve Windows), altyazı geçmişi (Mac, katkı: [rumpony](https://github.com/rumpony)).

**beta.17'de gelenler:** Mac uygulamasının macOS 26'da yeniden açılması, kapanıştaki Remote Play çökmesinin giderilmesi (Mac ve Windows), Mac'te disk dolu uyarısı.

**beta.16'da gelenler:** Ekranı çevir (Windows'ta Num 9, Mac'te ⌥⌘T: menüler, görev paneli ve bilgi kutuları TV'de kendi yerinde Türkçe), TV uygulaması 0.9.5, Windows'ta aynı hizadaki uzak yazıların ayrılması.

**beta.15'te gelenler:** birkaç çeviriyi deneyip en iyisini seçen Windows çevirisi, oyun ifadelerinin ("friendlies", "got your six") anlamıyla çevrilmesi, isteğe bağlı DeepL çevirisi (Mac ve Windows), Windows'ta indirmesiz hızlı Türkçe ses.

**beta.14'te gelenler:** Windows'ta İngilizce yazı tanıma paketi uyarısı ve tek düğmeyle kurulumu, menü/gösterge/kumanda ipuçlarının altyazıya karışmaması, yeni altyazı gelince eski dublaj cümlesinin kesilmesi, fareyle çizilen alan ve Num 1/2/3 ek bölgeler (katkı: [sadumqn](https://github.com/sadumqn)), Samsung'da eski kurulumun yeni sürümü engellememesi.

**İndirme**

| Dosya | Ne için |
|---|---|
| `CouchSubs-macos.dmg` | Mac kurulumu (Apple Silicon, macOS 26+) |
| `CouchSubs-windows-Setup.exe` | Windows 10 (2004) / 11 kurulumu, 64 bit |
| `CouchSubs-macos.zip`, `CouchSubs-windows-x64.zip` | Kurulumsuz kullanmak isteyenler için |
| `com.couchsubs.tv_…_all.ipk` | LG TV uygulaması, elle kurmak isteyenler için |
| `CouchSubs-samsung-kaynak.zip` | Samsung TV uygulaması, Tizen Studio ile elle kurmak isteyenler için |
| `CouchSubs-androidtv.apk` | Android TV / Google TV uygulaması, USB bellekle kurmak isteyenler için |
| `appcast.xml`, `releases.win.json`, `*.nupkg` | Uygulama içi güncelleme dosyaları (elle indirmeyin) |
| `SHA256SUMS.txt` | Dosyaların doğrulama özetleri |

**Bilinen durumlar**

- Windows kurulum dosyası imzasız: "Windows bilgisayarınızı korudu" uyarısında **Ek bilgi › Yine de çalıştır**. Uyarı yalnız ilk kurulumda çıkar; güncellemelerde çıkmaz.
- Dublaj yeni: sesin TV'den oyunun sesiyle birlikte çalması her TV'de henüz denenmedi. TV'den ses gelmezse ayarlardan sesi bilgisayara alın ve modelinizi yazın. Oyunun ses ayarlarından konuşma (diyalog) sesini kısın.
- Sesler yapaydır, gerçek kişilere ait değildir: Pocket TTS Turkish (wite-tech, CC BY 4.0), Kyutai'nin pocket-tts programıyla.
- Samsung ve Android TV kurulumu gerçek TV'de henüz az denendi. Sorun olursa TV modelinizle birlikte [Issues](https://github.com/yorulmazsinan/couchsubs/issues) sayfasından yazın; Windows'ta günlükler `%LOCALAPPDATA%\CouchSubs` klasöründe (`hata.log`, `remoteplay.log`).
- PS5 aynı anda tek Remote Play bağlantısına izin verdiği için oyun PS Remote Play uygulamasıyla oynanırken CouchSubs kullanılamaz; PS5'in TV'ye HDMI ile bağlı olması gerekir.
- Mağaza sürümleri henüz yok. Vestel, Arçelik, Beko ve Grundig'in Linux modelleri ile Philips Saphi ve Titan OS desteklenmiyor.

---

New beta. OBS overlay: streamers using a capture card can add a Browser source at `http://localhost:8787/obs` (1920×1080) and the Turkish subtitles appear over the game on screen and on stream, covering the English line when its position is known; the address is under Settings › OBS overlay. Subtitle history on Mac (contributed by rumpony): the last 50 translations of the session with their English text. Also includes beta.17's fixes (macOS 26 launch, Remote Play crash on disconnect, disk-full warning). Download: [Mac (DMG)](https://github.com/yorulmazsinan/couchsubs/releases/latest/download/CouchSubs-macos.dmg) · [Windows (installer)](https://github.com/yorulmazsinan/couchsubs/releases/latest/download/CouchSubs-windows-Setup.exe).
