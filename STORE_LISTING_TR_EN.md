# Microsoft Store — Kopyala-Yapıştır Mağaza Başvuru Metinleri (TR & EN)

Partner Center hesabınızı açtıktan sonra uygulama oluştururken aşağıdaki alanları doğrudan kopyalayıp yapıştırabilirsiniz.

---

## 1. Uygulama Adı (Reserve App Name)
`Circle Mix Record` *(Eğer doluysa alternatif: `Circle Mix Record Studio`)*

## 2. Fiyatlandırma & Yaş Sınırı
- **Kategori:** Fotoğraf ve Video (`Photo & video`) -> Alt kategori: Video
- **Yaş Derecelendirmesi (IARC):** Tüm Yaşlar (`3+` / `Everyone` — şiddet, kumar, kullanıcılar arası açık sosyal ağ içeriği yoktur)
- **Gizlilik Politikası URL'si:** `PRIVACY.md` dosyasını web sitenize (ör. `https://thezencirs.com/circle-mix-record-privacy`) veya GitHub sayfanıza koyup linkini yapıştırın.

---

## 3. Türkçe Mağaza Açıklaması (`tr-tr`)

### Kısa Açıklama (Short Description)
Tek tuşla aynı anda hem 16:9 Yatay (Circle Yatay/Masaüstü) hem 9:16 Dikey (Circle Dikey) ekran kaydı alan, tümleşik Circle Edit kurgu, otomatik sessizlik kesici ve 60 FPS Timelapse video stüdyosu.

### Uzun Açıklama (Description)
Circle Mix Record; içerik üreticileri, eğitmenler, mimarlar, 3B tasarımcılar ve yazılım geliştiriciler için sıfırdan tasarlanmış hepsi-bir-arada Windows kayıt ve kurgu stüdyosudur.

Ayrı ayrı ekran kaydedici, dikey kadraj kırpıcı ve video kurgu programı kullanma zahmetini ortadan kaldırır:

• Eşzamanlı İkili Tuval Kaydı (16:9 + 9:16 Dual-Canvas): Tek tuşla (Ctrl+Shift+R) aynı anda hem tam ekran 16:9 yatay videonuzu hem de fare imlecini akıllıca takip eden 9:16 dikey Circle Dikey videonuzu donanım hızlandırmalı (NVIDIA NVENC / Intel QSV / AMD AMF) olarak kaydeder.
• Circle Edit & 60 FPS Timelapse Stüdyosu: Kayıt bittiği anda arka plan ekran yakalama motoru sıfır işlemci kullanımına geçer ve tümleşik kurgu ekranı açılır. Videolarınızı sürükleyip bırakın, kesin (S), birleştirin veya 0.5x yavaş çekimden 30x hızlı çekime kadar tek tıkla 60 FPS akıcı Timelapse MP4 çıktısı alın.
• Circle Auto-Cut (Akıllı Sessizlik Kaldırıcı): Mikrofon kanalındaki sessiz beklemeleri otomatik tespit ederek tek tıkla keser (Ripple Delete) veya 3B modelleme/çizim kayıtlarında sessiz anları 8x hızlandırır.
• Stüdyo Kalitesinde Ses İşleme: Çift kanallı WASAPI masaüstü + mikrofon yakalama, gürültü azaltma ve -14 LUFS yayın standardında otomatik ses dengeleme.
• Circle Meet Canlı Oda: 6 haneli oda koduyla hızlı ekran paylaşımı ve yalnızca sizin izninizle çalışan etkileşimli sunum desteği.

### Özellikler (Features — Tek tek satır olarak eklenir)
- Tek tuşla eşzamanlı 16:9 Yatay ve 9:16 Dikey (Smart Portrait) 60 FPS ekran kaydı
- NVENC, Intel QuickSync ve AMD AMF GPU donanım hızlandırmalı yüksek kaliteli kayıt
- Tümleşik Circle Edit zaman çizelgesi: Kes, Sil, Geri Al, PIP bindirme ve müzik ekleme
- 0.5x ile 30x arası hız kontrolü ve tek tıkla 60 FPS Timelapse MP4 dışa aktarma
- Circle Auto-Cut ile konuşmasız/sessiz bölümleri otomatik kesme veya 8x hızlandırma
- Tamamen yerel çalışma: Kayıtlarınız bilgisayarınızdaki Videolar\CircleMixRecord klasörüne kaydedilir

---

## 4. English Store Listing (`en-us`)

### Short Description
All-in-one Windows studio that simultaneously records 16:9 Horizontal and smart cursor-tracking 9:16 Vertical video, complete with built-in Circle Edit, Auto-Cut silence removal, and 60 FPS Timelapse export.

### Description
Circle Mix Record is a native, hardware-accelerated Windows recording and editing studio built for creators, educators, 3D artists, architects, and developers.

Stop juggling separate screen recorders, vertical croppers, and heavy NLE editors:

• Simultaneous Dual-Canvas Recording (16:9 + 9:16): Press Ctrl+Shift+R once to capture both crisp 16:9 widescreen video (1080p / 4K UHD) and smart cursor-following 9:16 vertical video simultaneously using GPU hardware acceleration (NVIDIA NVENC, Intel QSV, AMD AMF, or Windows Media Foundation).
• Circle Edit & 60 FPS Timelapse Studio: The moment you stop recording, Circle Edit opens automatically with zero background CPU overhead. Drag and drop clips, split (S), trim, undo (Ctrl+Z), add audio tracks, or export silky-smooth 60 FPS Timelapses from 0.5x slow-motion up to 30x speed.
• Circle Auto-Cut (Smart Silence Remover): Automatically detects silent pauses in your microphone track to ripple-delete dead air or speed up silent 3D modeling/coding segments by 8x.
• Broadcast-Grade Audio: Dual WASAPI desktop + microphone capture with noise suppression and -14 LUFS loudness normalization.
• 100% Local Privacy: All recordings and projects are saved directly on your PC under Videos\CircleMixRecord with zero cloud uploads or telemetry.

---

## 5. Notes for Certification (Microsoft İnceleme Uzmanı İçin Not — ÇOK ÖNEMLİ)
*(Başvuru ekranındaki "Notes for certification" kutusuna aşağıdaki İngilizce notu yapıştırın; inceleme uzmanının `runFullTrust` iznini ilk seferde onaylamasını sağlar:)*

```text
Circle Mix Record is a native WPF (.NET 10 self-contained) desktop screen recorder and video editor.
- It uses restricted capability `runFullTrust` to capture desktop screen frames, global recording hotkeys (e.g., Ctrl+Shift+R), WASAPI loopback system audio, and invoke its bundled LGPL v3 FFmpeg encoder in `bin\ffmpeg.exe`.
- All user recordings and project files are written strictly to the user's `Videos\CircleMixRecord` folder.
- Camera and microphone capture only start when the user explicitly clicks the Record button ("● Kayıt") or enables camera preview.
- No login or external account is required to test all recording, editing, Auto-Cut, and Timelapse features.
```


