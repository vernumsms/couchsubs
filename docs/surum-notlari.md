**Altyazı+**: CouchSubs'ın **Claude yapay zekâ çevirili** sürümü (vernumsms fork'u). Orijinal proje: [yorulmazsinan/couchsubs](https://github.com/yorulmazsinan/couchsubs).

**İndir:** [Windows (kurulum)](https://github.com/vernumsms/couchsubs/releases/latest/download/CouchSubs-windows-Setup.exe)

**Bu sürümde yeni (claude.14): Altyazı+ — yeni ad, yeni görünüm**

- **Uygulamanın adı artık Altyazı+.** Yeni logo, koyu başlık çubuğu. Ayarlar, Claude anahtarı ve çeviri hafızası yerinde kalır.
- **TV'de modern altyazı.** Yumuşak köşeli şerit, ince çerçeve, konuşmacı adı sarı ("Kal:", "Park:"). Ayarlar → Altyazı → Görünüm'den eski düz kutuya dönülebilir. TV uygulaması 0.9.8.
- **Yenilenen ana ekran.** Oynanan oyunun adı, Dublaj ve Oyunu dinle için hızlı düğmeler, bu oturumda gösterilen satır sayısı.
- **Sekmeli Ayarlar.** Altyazı, Çeviri, Oyun, Dublaj, Gelişmiş. Açılır listeler ve onay kutuları koyu temaya uygun.

**claude.13'te gelenler: tekrarsız ve titremeyen altyazı**

- **Aynı altyazı iki kez çıkmaz.** Oyun önceki repliği ekranda tutup yenisini altına ekliyor; TV'de artık yalnız yeni satır çıkıyor.
- **Altyazı önce çıkıp hemen değişmez.** Claude'un çevirisi 2,5 saniye içinde gelirse yerel (hızlı) çeviri hiç gösterilmiyor; yalnız bağlantı yavaşsa önce yerel çeviri çıkıp sonra düzeliyor.
- **Solan altyazının bozuk okuması yeni satır sayılmaz.** "Tell me how he died" solarken "lell now he died" diye okununca Claude'dan uydurma bir cümle ("Şimdi öldü") geliyordu; artık önceki çeviri kalıyor.
- **Oyunu dinlemede uydurma sözler elenir.** Whisper, anlamadığı sesi (Korece konuşma, patlama) "I'll see you next time" gibi kalıp sözlere çeviriyordu; az emin olduğu metin atılıyor, güven puanı kayda yazılıyor.

**claude.12'de gelenler: oyunu dinleme (deneysel)**

- **Altyazısı olmayan konuşmalar da çevrilir.** Ayarlar → "Oyunu dinle (deneysel)": oyunun sesi de PS5'ten alınır, konuşmalar bu bilgisayarda Whisper ile yazıya çevrilir (hiçbir yere gönderilmez). Oyun o konuşmanın altyazısını göstermediyse Türkçesi TV'de çıkar. İlk açılışta 190 MB'lık model indirilir. Varsayılan kapalı.

**claude.11'de gelenler: çeviri hafızası, oyun bilgisi, doğru süre**

- **Kalıcı çeviri hafızası.** Claude'un çevirdiği her satır oyuna göre diske kaydedilir (%LOCALAPPDATA%\CouchSubs\hafiza). Bölümü baştan alınca, ölüp yeniden doğunca ya da uygulamayı kapatıp açınca aynı satırın Türkçesi beklemeden ve ücretsiz gelir.
- **Oyun bilgisi ve sözlük.** Ayarlar → "Oynadığınız oyun": oyunun adını yazınca Claude karakterleri ve terimleri baştan bilir. Notlar kutusuna sözlük yazılabilir ("Ghost = çevirme", "Price erkek").
- **Altyazı oyundaki kadar kalır.** Oyundaki satır 15 saniyeden uzun dursa da (uzun sahne, seçenek ekranı) Türkçesi kalkmaz; oyundaki kalkınca hemen kalkar.

**claude.10'da gelenler: dublaj kapalıyken de temiz altyazı**

- **Arayüz yazıları dublaj kapalıyken de gizlenir.** "SIRT ÇANTASI", "ENVANTER", "SIFIRLA SEKMESİ" gibi yazılar yalnız dublaj açıkken ayıklanıyordu; artık her zaman ayıklanıyor.
- **Claude'un açıklamaları altyazıda çıkmaz.** Okunamayan satırda "I can't make out this line…" gibi açıklama yazılıyordu; artık satır boş kalıyor.
- **Dublaj zamanlaması kayda yazılır** (dublaj-sira, dublaj-hazirlandi, dublaj-caliyor): dublaj gecikmesinin nerede oluştuğu ölçülebiliyor.

**claude.9'da gelenler: doğru konuşmacı adı**

- **Hitap konuşmacı adı sanılmaz.** "Valeria Luca. They're still on us" satırında konuşan Valeria'dır, "Luca." hitaptır; eskiden ad "Valeria Luca" sanılıyor, sonraki replikler de "Valeria Luca:" diye yazılıyor ve ayrı bir ses alıyordu.

**claude.8'de gelenler: çakışmayan istekler, temiz ekran**

- **Aynı satır iki kez çevrilmez.** Önceki blok hâlâ çevrilirken yeni satır gelince ortak satırlar yeniden gönderiliyordu; artık ilk isteğin sonucu bekleniyor, Claude'a yalnız gerçekten yeni satır gidiyor.
- **Oyun arayüzü yazıları TV'de görünmez.** "BIPOD", "ARMOR PLATE / FULL", silah adları gibi yazılar seslendirilmiyordu ama altyazı olarak çıkıyordu; artık gösterilmiyor, hızlı çeviriyle göründüyse kaldırılıyor.

**claude.7'de gelenler: satır önbelleği, daha hızlı çeviri**

- **Yalnız yeni satır çevrilir.** Oyun önceki replikleri ekranda tutup yenisini altına eklediğinde (dört satıra kadar) eskiden bütün blok yeniden çevriliyordu; dört satırda yanıt 2,3 sn sürüyordu. Artık önceden çevrilmiş satırlar önbellekten geliyor, Claude'a yalnız yeni satır gidiyor (öncekiler bağlam olarak): gecikme ve maliyet düşüyor. Bütün satırlar önbellekteyse Claude'a hiç gidilmiyor.
- **Satıra yapışık kumanda ipuçları temizlenir** ("— MOUNT Don't need to see you…", "RS MOUNT", "EQUIP").

**claude.6'da gelenler: sakin altyazı, arka plan sesleri**

- **Altyazı titremez.** Çevirisi süren replik yeniden okunduğunda (konuşmacı adı bir görünüp bir kayboluyor) yeni çeviri istenmiyor; denemede aynı replik bir saniyede yedi kez çevriliyor, altyazı her seferinde değişiyordu.
- **Bozuk okumalar ekrana çıkmaz.** Anlık modda her satır iki kare aynı okunmadan kabul edilmiyor (≈50 ms); yazı belirirken okunan çöp satırlar eleniyor. Claude da okunamayan satırı uydurmak yerine boş bırakıyor.
- **Arka plan sesleri aynı anda.** Telsiz konuşması, uzaktan bağırış, TV'deki spiker gibi arka plan replikleri sıra beklemeden ana repliğin üstüne daha kısık sesle karışıyor; dublaj kalabalık sahnelerde geride kalmıyor.

**claude.5'te gelenler: gecikmeyen dublaj**

- **Ekranda kalan satır yeniden seslendirilmez.** Call of Duty önceki repliği ekranda tutup yenisini altına ekliyor; her satır iki kez seslendirildiği için ses giderek geride kalıyordu. Artık her satır bir kez okunur.
- **Bozuk okunan satırlar ayrı replik sayılmaz.** Yazı belirirken yanlış okunan uzun satırlar ("There's mure ccminq…") aynı replik olarak tanınıyor.
- **Claude'un birleştirdiği satırlar kullanılır.** Yarım kalan iki satırı tek cümleye birleştiren yanıtlar eskiden atılıyordu.
- **Kumanda ipuçları altyazıda görünmez** ("MOUNT", "EQUIP"); ekranda yalnız arayüz yazısı varsa (ör. "NEW YORK CITY") gösterilir.
- **Daha kısa yanıt:** seslendirme metni altyazıyla aynıysa Claude onu yeniden yazmıyor; yanıt daha çabuk geliyor.

**claude.4'te gelenler: hızlı sahnelerde dublaj ve altyazı (Call of Duty denemesinden)**

- **Dublaj artık yarıda kesilmez.** Yeni replik gelince çalan cümle tamamlanır, bekleyen eski cümleler atlanır ve hemen yeni repliğe geçilir.
- **Aynı replik bir kez seslendirilir.** Altyazı titreyince, yazı tanıma bir harfi farklı okuyunca ya da konuşmacı adı yazıdan sonra belirince aynı replik yeniden çevrilmiyor ve baştan seslendirilmiyor (denemede bir replik 6 kez seslendirilmişti).
- **Mermi sayacı ve göstergeler okunmaz.** Rakamı harfinden çok satırlar atılıyor; Claude oyun arayüzü yazılarını (silah adı, kayıt bildirimi) seslendirmiyor.
- **Aynı anda konuşan birden çok kişi**, araları açık olsa da alt alta, oyundaki sırayla gösteriliyor.
- **İki noktasız konuşmacı adları** ("Lt. West Okay, listen up.") tanınıyor: altyazı "Lt. West: Tamam, dinleyin." olarak yazılıyor ve her karakter kendi sesiyle konuşuyor.
- **Yanlış kadın sesi düzeltildi.** Claude cinsiyeti yalnız kesin olduğunda söylüyor ("Moon", "Jae" gibi belirsiz adlarda söylemiyor); bilinmeyen karakterler anlatıcıyla aynı cinsiyette ses alıyor.

**claude.3'te gelenler: akıllı Türkçe dublaj**

- **Duyguyu Claude seçer.** Dublaj açıkken Claude her replik için sahneye ve önceki satırlara bakarak tonu belirler (mutlu, üzgün, kızgın, şaşkın, sakin, nötr); ses o tonda okunur. Önceden ton yalnız İngilizce metindeki anahtar sözcüklerden tahmin ediliyordu.
- **Doğru ses, doğru karakter.** Claude konuşanın cinsiyetini addan ve bağlamdan tahmin eder; listede olmayan karakterlere ve etiketsiz satırlara uygun ses atanır.
- **Seslendirmeye özel kısa metin.** Altyazı tam çeviri olarak kalır; ses ise aynı anlamı daha kısa, konuşma diliyle söyler, böylece oyunun gerisinde kalmaz. Sayılar yazıyla okunur.
- Claude'un bu modelde yapılandırılmış yanıtı reddetmesi durumunda dublaj düz çeviriyle çalışmayı sürdürür.

**claude.2'de gelenler**

- **Alt alta satırlar oyundaki sırayla.** Oyunda alt alta duran ayrı cümleler, konuşmacılar ve seçenekler TV'de de ayrı satırlarda, yukarıdan aşağıya aynı sırayla gösterilir; yalnız alt satıra taşan cümle tek satırda birleşir. Claude satır sayısını ya da sırasını bozarsa o çeviri kullanılmaz. TV uygulaması 0.9.7 (CouchSubs çeviriyi başlatınca kendisi kurar).
- **Claude çevirisinde dublaj konuşmacıları ayırır:** "MOON: …" ve "KRATOS: …" satırları her karakterin kendi sesiyle okunur.
- **Çeviri günlüğü:** %LOCALAPPDATA%\CouchSubs\ceviri.log dosyasında her satırın Claude çevirisi ve yanıt süresi tutulur (yalnız bu bilgisayarda).

**claude.1'de gelenler**

- **Yapay zekâ çevirisi (Claude).** Bilgisayardaki çeviri modeli her satırı hemen gösterir; bir an sonra Claude onu deyimleri, argoyu ve bağlamı anlayan doğal Türkçeyle değiştirir ("We're gonna call his bluff" → "Blöfünü göreceğiz"). Önceki satırları ve konuşmacı adlarını bağlam olarak kullanır; seçenek listelerini satır satır çevirir; yazı tanıma hatalarını ("cali" → "call") düzeltir. Ayarlar › Yapay zekâ çevirisi'nden kendi Anthropic anahtarınızla açılır; varsayılan model Claude Haiku 5.5 (hızlı, düşük maliyet); en iyi kalite için Claude Opus 5.5 seçilebilir. Claude'a ulaşılamazsa bilgisayardaki çeviri kalır.
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
