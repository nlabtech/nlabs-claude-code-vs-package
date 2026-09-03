# Claude Code için Visual Studio Eklentisi (nLabtech)

Visual Studio'da açık olan solution'ı Claude Code CLI'ye bağlayan bir köprü eklentisi.
Seçtiğin kod, açık dosyalar ve derleyici hataları Claude'a otomatik gider — Visual
Studio'dan hiç çıkmadan.

> **Bağımsız / topluluk projesi.** Anthropic ya da Microsoft ile resmi bir bağlantısı
> yoktur. "Claude Code", "Anthropic" ve "Visual Studio" ilgili sahiplerinin
> markalarıdır. Bu eklenti kişisel bir ihtiyaçtan doğdu: Claude Code'un resmi bir
> Visual Studio desteği yok, ben de kendim için yazdım.

## Bu depo nasıl büyüyor

Bu eklenti, adım adım ve herkese açık (build-in-public) geliştirildi. Her önemli adım,
LinkedIn'de paylaşılan bir yazıyla birlikte geldi; ilgili commit, o yazıda anlatılan kodu
**birebir** içerir. Böylece anlatılan ders ile çalışan kod her zaman aynı yerde durur.

Yazılar: [@turkmvc](https://www.linkedin.com/in/turkmvc/) · nLabtech ([nlabtech.com.tr](https://nlabtech.com.tr))

## Ne yapar

- **Seçilen kod kendiliğinden bağlam olur** — dosya ya da satır sormaz.
- **Derleme hatalarını Visual Studio'dan okur** — Error List'teki listeyi.
- **Değişiklikleri Visual Studio'nun diff penceresinde gösterir** — kabul et / reddet.
- Model çalıştırmaz, dosya yazmaz, telemetri göndermez; yalnızca `127.0.0.1` dinler ve
  kullanıcının kendi Claude aboneliğini kendi makinesinde kullanır.

## Teknoloji

- SDK tarzı VSSDK eklentisi, süreç içi (in-proc), hedef `net472` — Visual Studio'nun
  kabuğu hâlâ .NET Framework 4.7.2 üzerinde koşar.
- Tek VSIX hem Visual Studio 2022'ye hem 2026'ya kurulur (`InstallationTarget [17.0,)`).
- Çekirdek (WebSocket sunucusu, el sıkışma, protokol) Visual Studio'ya sıfır bağımlı
  ayrı bir projede durur — VS açmadan test edilebilir.

## Durum

Erken aşama, herkese açık gelişiyor. Üretim kullanımından önce kendi riskinle dene.

## Lisans

MIT — bkz. [LICENSE](LICENSE).
