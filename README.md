# 📻 Sprawdzanie listy stacji radiowych

Aplikacja Windows do pobierania, sprawdzania i zarządzania listami stacji radiowych dla urządzenia **yoRadio**.

![Version](https://img.shields.io/github/v/release/seba99317/sprawdzanie-list?label=wersja)

---

## ✨ Funkcje

- 🌐 **Pobieranie stacji z wielu źródeł jednocześnie:**
  - Radio Browser (57 000+ stacji)
  - SHOUTcast (5 000+ stacji)
  - yoRadio
  - SomaFM
  - Internet-Radio
  - rcast.net
  - OnlineRadioBox

- ✅ **Sprawdzanie streamów** — testuje czy stacja działa (HEAD/GET, timeout 2s, 800 równoległych połączeń)

- 🎵 **Wbudowany odtwarzacz** — podgląd stacji przed dodaniem (LibVLC, obsługuje MP3/AAC/OGG/HLS)

- 💾 **Cache** — wyniki sprawdzania zapisywane lokalnie, kolejne uruchomienie błyskawiczne

- 🔄 **Auto-update** — aplikacja sama się aktualizuje gdy pojawi się nowa wersja

- 📡 **Integracja z yoRadio** — wysyłanie gotowej listy bezpośrednio do urządzenia przez sieć lokalną

- 🔍 **Wyszukiwanie i filtrowanie** — pole filtra w oknie wyboru stacji

---

## 📥 Pobieranie

Pobierz najnowszą wersję ze strony [**Releases**](https://github.com/seba99317/sprawdzanie-list/releases) lub kliknij poniżej:

➡️ **[Pobierz sprawdzanie list.exe](https://github.com/seba99317/sprawdzanie-list/releases/latest)**

> Aplikacja działa na **Windows 10/11** (64-bit). Nie wymaga instalacji — wystarczy uruchomić exe.

---

## 🚀 Jak używać

1. Uruchom `sprawdzanie list.exe`
2. Kliknij **Pobierz stacje** — aplikacja pobierze listy ze wszystkich źródeł i sprawdzi które działają
3. W oknie wyników użyj pola **Filtr / szukaj** aby znaleźć interesujące stacje
4. Kliknij ▶ przy stacji aby odsłuchać podgląd
5. Zaznacz stacje checkboxem i kliknij **Dodaj**
6. Kliknij **Wyślij listę do radia** aby wysłać do urządzenia yoRadio

---

## 🔧 Wymagania

- Windows 10 lub nowszy
- Sieć internetowa (do pobierania list)
- Urządzenie yoRadio w sieci lokalnej (opcjonalne)

---

## 📝 Autor

**seba99317** — [GitHub](https://github.com/seba99317)

☕ [Postaw kawę](https://buycoffee.to/seba99317)

---

## 📋 Changelog

### v1.1
- Wbudowany odtwarzacz LibVLC (MP3, AAC, OGG, HLS, FLAC*)
- Auto-update — aplikacja sama się aktualizuje
- Cache stacji (24h) — szybki start
- Okno wyników otwiera się natychmiast, stacje pojawiają się na żywo
- Przycisk ▶ przy każdej stacji w oknie wyboru
- Pasek odtwarzacza z bitrate, statusem i głośnością

### v1.0
- Pierwsze wydanie
- Pobieranie z Radio Browser, yoRadio, SomaFM, SHOUTcast, Internet-Radio, rcast.net, OnlineRadioBox
- Sprawdzanie streamów równolegle
- Integracja z yoRadio
