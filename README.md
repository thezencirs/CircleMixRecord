# CircleMixRecord

**Windows için tek tuşla çift tuval (`16:9` Yatay + `9:16` Dikey) ekran kaydedici, 6 haneli kodla canlı toplantı / uzaktan bağlantı (`Circle Meet`) ve tümleşik video kurgu + `60 FPS Timelapse` stüdyosu.**

> **Geliştirici:** [thezencirs](https://github.com/thezencirs) — MIT Lisansı altında açık kaynak olarak sunulmuştur.
>
> **English:** Native Windows studio application combining simultaneous **16:9 Horizontal + 9:16 Vertical (Smart Portrait)** hardware-accelerated screen recording, **Circle Meet** (6-digit code live room & remote control), and **Circle Edit & Timelapse** (drag-and-drop non-destructive video editor, auto-cut silence, PIP masking, and up to 30x 60 FPS Timelapse export). Built by **thezencirs**.

![platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20(x64)-261A66) ![brand](https://img.shields.io/badge/Circle%20Palette-%23FFFFFF%20%7C%20%23EF5F18%20%7C%20%23261A66-EF5F18) ![license](https://img.shields.io/badge/license-MIT-FFFFFF) ![author](https://img.shields.io/badge/author-thezencirs-EF5F18)

---

## Öne Çıkan Özellikler

1. **Circle Dual-Canvas Kayıt (`16:9` Yatay + `9:16` Dikey Eşzamanlı):**
   - Tek tuşla (`Ctrl + Shift + R`) aynı anda hem geniş ekran (`1920x1080` veya `3840x2160 4K UHD`) hem de imleç takipli dikey (`1080x1920`) kayıt alır.
   - Yatay ve dikey videoları otomatik olarak ayrı klasörlere (`Yatay_16x9_Kayitlar\` ve `Dikey_9x16_Reels_Kayitlar\`) ayırır.
   - `WriteableBitmap` sıfır-tahsisatlı canlı önizleme ve `NVENC / QSV / AMF` donanım kodlama ile bilgisayarı yormadan yüksek kalitede (`CQP 16`) kayıt yapar.
2. **Circle Meet & Uzaktan Bağlantı (6 Haneli Kod):**
   - Ek bir uzak masaüstü veya toplantı yazılımına ihtiyaç duymadan 6 haneli Circle koduyla canlı sınıf/toplantı açar.
   - İsteğe bağlı `Circle Remote` izniyle karşı tarafın fare ve klavye ile ekranınıza yardımcı olmasına olanak tanır.
3. **Circle Edit & Timelapse Kurgu Stüdyosu:**
   - Kayıt bittiği anda arka plan ekran yakalama motorunu uyku moduna (`0% CPU`) alır ve doğrudan **Circle Edit** kurgu ekranını açar.
   - Sol panelde her zaman açık duran **Yatay (`16:9`)** ve **Dikey (`9:16`)** klasörlerinden sürükle-bırak (`Drag & Drop`) ile video, ses (`.mp3/.wav`) ve görsel bindirme (`PIP`) eklenir.
   - **Tam Kurgu Araçları:** Oynat/Duraklat (`Space`), Kes/Böl (`S` / `Ctrl+B`), Sil (`Delete`), Geri Al (`Ctrl+Z`), Klip Çoğalt.
   - **Timelapse Motoru:** `0.5x` yavaş çekimden `2x`, `4x`, `8x`, `16x` ve `30x` hızlı çekime kadar anlık hızlandırma ve tek tıkla **60 FPS Timelapse MP4** dışa aktarma.
   - **Circle Auto-Cut (Akıllı Sessizlik Kaldırıcı):** Mikrofon kanalındaki sessiz boşlukları tespit edip tek tıkla keser (`Ripple Delete`) veya 3B modelleme videoları için konuşmasız bölümleri `8x` hızlandırır.
   - **Kayıpsız Dikey Kadraj (`Non-Destructive Uncrop`):** Kayıt sonrasında bile dikey videonun yakınlaştırma/kadraj oranını bozulmadan yeniden üretir.
4. **Resmi Circle 3 Renk Tasarım Standardı:**
   - **WHITE (`#FFFFFF`)** • **ORANGE (`#EF5F18`)** • **SCARLET BIKINI (`#261A66`)**

---

## Global Klavye Kısayolları

| Kısayol | İşlev |
| :--- | :--- |
| **`Ctrl + Shift + R`** | İkili Kaydı Başlat / Bitir (`16:9` + `9:16`) ve Kurguya Geç |
| **`Ctrl + Shift + V`** | Yalnızca Dikey (`9:16`) Kayıt Başlat / Bitir |
| **`Ctrl + Shift + P`** | Kaydı Duraklat / Devam Et |
| **`Ctrl + Shift + N`** | Kaydı Durdurmadan Yeni Part'a Böl |
| **`Ctrl + Shift + K`** / **`Alt + S`** | Net Kapak Fotoğrafı / Ekran Resmi Al (Panoya + Klasöre) |
| **`Ctrl + Shift + M`** / **`Alt + M`** | Zaman Çizelgesine Bölüm İşaretçisi (`Marker`) Ekle |
| **`Alt + Q`** | Fare İmleci Odaklı `2x Smart-Zoom` Aç / Kapat |
| **`Alt + E`** | İmleç Takibini Sabitle / Serbest Bırak |
| **`Alt + Shift + C`** | Köşe Kamerayı Anında Göster / Gizle |
| **`Alt + Shift + M`** | Mikrofonu Sustur / Aç |
| **`Alt + Shift + D`** | Masaüstü Sesini Sustur / Aç |
| **`Alt + Shift + 1..4`** | Sahneler Arası Anlık Geçiş |
| **`Space` / `S` / `Del` / `Ctrl+Z`** | Circle Edit: Oynat/Duraklat, Kes (`Split`), Sil, Geri Al |

---

## Kurulum ve Çalıştırma

1. [**Releases**](../../releases/latest) sayfasından `CircleMixRecord-vX.Y.Z-win-x64.zip` paketini indirin ve bir klasöre çıkarın.
2. Donanım hızlandırmalı MP4/MKV video kodlama ve Timelapse çıktıları için sisteminizde `ffmpeg.exe` bulunmalıdır (`PATH` üzerinde veya uygulama klasöründeki `bin\ffmpeg.exe` konumunda).
3. `CircleMixRecord.exe` dosyasına çift tıklayarak stüdyoyu başlatın.

---

## Kaynak Koddan Derleme

Gereksinim: **.NET 10 SDK (Windows x64)**

```powershell
git clone https://github.com/thezencirs/CircleMixRecord.git
cd CircleMixRecord
powershell -ExecutionPolicy Bypass -File build.ps1
# Çıktılar: dist\CircleMixRecord.exe, dist\CircleMixRecord-v1.0.0-win-x64.zip, dist\SHA256SUMS.txt
```

---

## Yasal Bildirim ve Açık Kaynak Bileşenler

- **CircleMixRecord** tamamen bağımsız bir açık kaynak projesidir ve **thezencirs** tarafından geliştirilmiştir. Herhangi bir üçüncü parti şirket veya ticari marka ile bağlantılı değildir.
- Video kodlama ve medya işleme süreçlerinde sistemde kurulu olan veya `bin\ffmpeg.exe` altında sağlanan **FFmpeg** komut satırı aracını standart işlem (`Process`) olarak çağırır. FFmpeg kendi açık kaynak lisansı (LGPL v2.1+ / GPL) kapsamındadır ve bu depoda FFmpeg ikili dosyası dağıtılmaz.

---

## Lisans

[MIT](LICENSE) © 2026 [thezencirs](https://github.com/thezencirs)
