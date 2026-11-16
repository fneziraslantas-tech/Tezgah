# Güvenlik denetimi — Kasım 2026

<p><b>Türkçe</b> · <a href="#english-summary">English summary</a></p>

Bu belge, Tezgah'ın ilk herkese açık sürümünden ([v0.1.1](https://github.com/fneziraslantas-tech/Tezgah/releases/tag/v0.1.1))
önce yapılan iki güvenlik denetimini, kapsamlarını, sınırlarını ve sonuçlarını özetler. Tehdit modeli ve açık bildirme
yolu için: [SECURITY.md](../SECURITY.md).

## 1. Yöntem

### 1.1 İç çekişmeli denetim

Kod herkese açılmadan önce, saldırgan gözüyle çalışan birden çok ajanla **3 tur** çekişmeli inceleme yapıldı. Her bulgu
bir regresyon testiyle birlikte düzeltildi. Bu turların sonucu bugün SECURITY.md'deki korumalardır; örneğin:

- güvenilmeyen repolar git süreci başlatılmadan, `.git` dosyaları doğrudan okunarak incelenir;
- git çalıştırılmadan önce `GitRepoGuard` ağ paylaşımına ya da aygıt yoluna giden her konumu reddeder;
- her dosya işlemi tek bir yol koruyucusundan (`PathGuard`) geçer;
- kaydedilmemiş iş varsa kaldırma için öğenin adı yazılarak onaylanır;
- denetim kaydı (`audit.log`) yazılamazsa hiçbir şey silinmez; varsayılan silme hedefi Geri Dönüşüm Kutusu'dur.

### 1.2 Dış yöntemle denetim: cloudflare/security-audit-skill

| Alan | Değer |
|---|---|
| Yöntem | [cloudflare/security-audit-skill](https://github.com/cloudflare/security-audit-skill), commit `c1c8a8c1471069fb0e188eeaff69b8e8db6564a8` |
| Mod / profil | Tam denetim (full audit), `standard` profil, tüm depo |
| Denetlenen kaynak | `c0c4a18` (#1–#10 birleştirilmeden önceki `main`) |
| Yürütme politikası | Yalnızca kaynak ve yerel kanıt (`sandboxed-source-and-local-only`) |

Aşamalar:

1. **Keşif** — 4 keşif ajanı ve 1 planlayıcı mimariyi, güven sınırlarını ve kapsam birimlerini çıkardı.
2. **Kapsam döngüsü (3 dalga + eleştirmenler)** — avcı ajanlar kapsam birimlerini inceledi: 1. dalgada 13 ajan
   (48 birim), 2. ve 3. dalgada 2'şer ajan. Her dalgadan sonra bir kapsam eleştirmeni eksik birim aradı; 1. ve 2.
   eleştirmen 2'şer yeni birim ekledi, 3. eleştirmen ve ondan bağımsız çalışan son ("final-clean") eleştirmen eksik
   birim bulmadı. Toplam **52 kapsam birimi**.
3. **Bağımsız doğrulama** — her aday kayıt, onu bulan ajandan bağımsız doğrulayıcılara verildi (10 doğrulayıcı +
   şemaya uymayan bir sonuç için 1 yeniden çalıştırma), ardından son doğrulama turu (9 ajan; esaslı değiştirilen bir
   kayıt ayrıca bağımsız biçimde yeniden üretildi).
4. **Doğrulayıcılar** — `findings.json` ve `coverage-ledger.json` skill'in şema doğrulayıcılarından geçti. Skill'in
   doğrulayıcı komut satırı araçları Windows'ta çalışmadığı için, aynı doğrulama fonksiyonları bir sarmalayıcı
   üzerinden çağrıldı.

## 2. Kapsam ve sınırlar

- **Hedef kod çalıştırılamadı.** Denetim Windows 11 üzerinde yapıldı ve bu makinede işletim sistemi düzeyinde
  yalıtılmış bir ortam (ağsız, boş ortam değişkenli, hedefi salt okunur tutan, kaynak sınırlı sandbox) yoktu. Bu yüzden
  restore, build, test ve uygulamanın kendisi denetim sırasında çalıştırılmadı; çalışma zamanına bağlı her belirleyici
  olgu, ilgili kayıtta "doğrulama gerekiyor" (`needs_validation`) olarak bırakıldı.
- **İncelenen alanlar:** masaüstü ve yerel IPC; tedarik zinciri ve sürüm; bellek güvenliği ve ikili dosya; kaynak
  tüketimi ve erişilebilirlik; veri yalıtımı ve yaşam döngüsü.
- **Kapsam dışı alanlar:** tarayıcı/webview, HTTP sunucusu ve kimlik doğrulama, yapay zekâ/LLM, bulut ve dağıtım, RPC
  ve mesajlaşma — Tezgah'ta bunların hiçbiri yok.
- **Değerlendirilmeyenler:** GitHub tarafındaki ayarlar (dal/etiket korumaları, environment korumaları, depo sırları);
  git, Windows ve kabuk entegrasyonlarının çalışma zamanı davranışı; SECURITY.md'nin kapsam dışı saydığı senaryolar
  (aynı kullanıcı, yönetici hakları, kullanıcının güvendiği projenin kendi kodu).
- 52 birimden 1'i engelli kaldı: bozuk biçimli tarama girdilerinin (ör. eşleşmemiş UTF-16 karakter içeren klasör adı)
  katalog kaydını nasıl etkilediği, serileştiriciyi çalıştırmayı gerektirdiği için gözlemlenemedi.

## 3. Sonuçlar

| Sonuç | Sayı |
|---|---|
| Doğrulanmış (`confirmed`) | 0 |
| Doğrulama gerekiyor (`needs_validation`) | 8 — hepsi düzeltildi |
| Reddedilen (`rejected`) | 2 |

### 3.1 Doğrulama bekleyen kayıtlar ve düzeltmeleri

Bunlar kaynakta izlenmiş, ama çalışma zamanında doğrulanamamış sınır hipotezleridir; önem derecesi taşımazlar.
Doğrulanmalarını beklemeden hepsi düzeltildi ve her düzeltme regresyon testleriyle geldi.

| # | Sorun (sade anlatım) | Etkilenen alan | Düzeltme |
|---|---|---|---|
| 1 | Kalıcı silme, alt klasörlerin bağlantı olup olmadığını yol üzerinden denetleyip aynı yolu yeniden açıyordu; silme sırasında bir klasör junction ile değiştirilirse silme başka bir yere yönelebilirdi. | Kalıcı silme (`FileRemover`) | [#5](https://github.com/fneziraslantas-tech/Tezgah/pull/5) — silme, açılmış tanıtıcılar (handle) üzerinden ve bağlantıları izlemeden ilerler |
| 2 | Git yapılandırmasındaki yol değerlerinde `:(optional)` öneki, ağ paylaşımı (UNC) denetimini atlatabiliyordu; güvenilmeyen bir repo taranırken NTLM özeti sızabilirdi. | Git koruması (`GitRepoGuard`) | [#8](https://github.com/fneziraslantas-tech/Tezgah/pull/8) — `:(optional)` ve `%(prefix)` önekleri her sırada soyulup denetlenir |
| 3 | Pasif tarama, repo dosyalarını ağ paylaşımına giden sembolik bağlantılar üzerinden okuyabiliyordu; güven verilmeden NTLM sızabilirdi. | Keşif (`FsUtil`, `FileSystemProvider`) | [#8](https://github.com/fneziraslantas-tech/Tezgah/pull/8) — bağlantı üzerinden dosya okunmaz; klonlamada sembolik bağlantılar kapalı |
| 4 | Göreli ya da yerel görünen git yol değerleri, çalışma ağacındaki bir bağlantı üzerinden ağ paylaşımına ulaşabiliyordu. | Git koruması ve yerel git çağrıları | [#8](https://github.com/fneziraslantas-tech/Tezgah/pull/8) — yol değerleri git gibi çözülür, bağlantı ya da yerel olmayan hedef içeren repo reddedilir; yerel git çağrılarında bu değerler sabitlenir |
| 5 | Güven penceresi eski komutu gösterirken, onaydan sonra aynı kimlikli güncel profil çalışabiliyordu. | Güven onayı ve çalıştırma (`MainViewModel`, `RunService`) | [#9](https://github.com/fneziraslantas-tech/Tezgah/pull/9) — yalnızca gösterilen komut çalışır; komut değiştiyse başlatma reddedilir |
| 6 | Güven, klasörün oluşturulma zamanına bağlıydı; aynı yolda yeniden oluşturulan farklı bir klasör (dosya sistemi tunneling'i) güveni devralabilirdi. | Güven çapası (`CatalogService`) | [#9](https://github.com/fneziraslantas-tech/Tezgah/pull/9) — güven birim seri numarası + dosya kimliği + oluşturulma zamanına bağlı; eski kayıtlar için bir kez yeniden sorulur |
| 7 | Bazı proje algılayıcı düzenli ifadeleri, yalnızca satır sonlarından oluşan bir dosyada karesel zaman alıyor ve zaman aşımı içermiyordu; taranan bir klasör keşfi uzun süre durdurabilirdi. | Proje algılayıcıları | [#6](https://github.com/fneziraslantas-tech/Tezgah/pull/6) — desenler doğrusal, her birine zaman aşımı eklendi |
| 8 | Güvenilmeyen, Tezgah ile klonlanmış bir klasör değiştirildiğinde eski "Tezgah tarafından klonlandı" işareti kalıyordu; güven penceresi farklı kodu bu etiketle gösterebilirdi. | Katalog ve güven penceresi | [#9](https://github.com/fneziraslantas-tech/Tezgah/pull/9) — klon işareti klasör kimliğine bağlı; klasör değişince düşer |

Raporun bulgu sayılmayan sertleştirme notlarından ikisi de ayrıca uygulandı:
[#7](https://github.com/fneziraslantas-tech/Tezgah/pull/7) — kaldırma yalnızca planda gösterilen süreçleri durdurur;
alan açma penceresi, silme kalıcıysa uyarır. Diğer sertleştirme notları bulgu değildir ve bu belgede ele alınmaz.

### 3.2 Reddedilen kayıtlar

| Kayıt | Neden reddedildi |
|---|---|
| Doğmamış HEAD'li bir repoda, diğer dallardaki push edilmemiş işin sayılmaması | Durumu yalnızca kullanıcının kendisi oluşturabilir ve silme yine kullanıcının onayıyla yapılır; bir güvenilirlik/kullanılabilirlik hatasıdır, güven sınırını aşmaz. |
| Güven onayının, eşzamanlı bir taramanın birleştirdiği klasör kimliğine bağlanabilmesi | Klasörü değiştirebilen biri aynı sonuca dosyaları yerinde düzenleyerek de ulaşır; çapaya özgü ek etki yok. Kalan kök neden (gösterilen komutun çalışana bağlanmaması) ayrı kayıt olarak izlendi ve #9'da düzeltildi. |

## 4. Bu denetim neyi kanıtlamaz

- **"0 doğrulanmış bulgu" bu çalıştırmanın bir özelliğidir, açık olmadığının kanıtı değildir.** Doğrulamayı engelleyen,
  bulgunun yokluğu değil, hedef kodu güvenle çalıştıracak yalıtılmış ortamın yokluğuydu.
- 8 kaydın gerçekten istismar edilebilir olup olmadığı çalışma zamanında doğrulanmadı; düzeltmeler ihtiyatla yapıldı.
- Kapsam dışı alanlar, GitHub depo ayarları ve üçüncü taraf yazılımların (git, Windows, kabuk entegrasyonları)
  davranışı değerlendirilmedi.
- Denetim belirli bir kaynak sürümüne (`c0c4a18`) aittir; sonraki değişiklikler bu denetimin kapsamında değildir.
- Bir açık bulursan: [SECURITY.md](../SECURITY.md) → GitHub Security sekmesi üzerinden özel bildirim.

---

<a id="english-summary"></a>

## English summary

Two security audits ran before Tezgah's first public release,
[v0.1.1](https://github.com/fneziraslantas-tech/Tezgah/releases/tag/v0.1.1).

**Internal adversarial audit.** A multi-agent adversarial review in 3 rounds before the code went public; every finding
was fixed with a regression test. It produced the protections described in [SECURITY.md](../SECURITY.md): untrusted
repositories are inspected without starting git, `GitRepoGuard` runs before any git process, every file action goes
through `PathGuard`, removals with unsaved work need typed confirmation, nothing is deleted without an audit record, and
the Recycle Bin is the default.

**External-method audit.** [cloudflare/security-audit-skill](https://github.com/cloudflare/security-audit-skill) at
commit `c1c8a8c1471069fb0e188eeaff69b8e8db6564a8`, full-audit mode, `standard` profile, whole repository, source
`c0c4a18`. Phases: reconnaissance (4 recon agents + planner); a coverage loop of 3 hunter waves (13, 2 and 2 agents),
each followed by a coverage critic, plus an independent final-clean critic — the last two found no missing units;
52 coverage units in total; independent verification of every candidate (10 verifiers + 1 re-run, then a final round
of 9 agents); `findings.json` and `coverage-ledger.json` passed the skill's schema validators (called through a wrapper
because the skill's validator CLIs do not run on Windows).

**Scope and limits.** Target code was never executed: the Windows host had no OS-enforced sandbox, so runtime facts
were left as `needs_validation`. Out of scope: browser/webview, HTTP and auth, AI/LLM, cloud, RPC/messaging (none exist
in Tezgah), GitHub-side settings, runtime behaviour of git/Windows/shell integrations, and the cases SECURITY.md
excludes. One of 52 coverage units stayed blocked (how malformed scan input such as an unpaired UTF-16 surrogate in a
folder name affects saving the catalog).

**Results: 0 confirmed, 8 needs_validation (all fixed), 2 rejected.**

| # | Lead | Area | Fix |
|---|---|---|---|
| 1 | Permanent delete checked subfolders for links by path and reopened them by path; a junction swapped in mid-delete could redirect it | `FileRemover` | [#5](https://github.com/fneziraslantas-tech/Tezgah/pull/5) |
| 2 | `:(optional)` prefix in git pathname values bypassed the UNC check (NTLM leak on untrusted repos) | `GitRepoGuard` | [#8](https://github.com/fneziraslantas-tech/Tezgah/pull/8) |
| 3 | Passive scan read repo files through symlinks to network shares (NTLM leak before trust) | Discovery | [#8](https://github.com/fneziraslantas-tech/Tezgah/pull/8) |
| 4 | Relative/local git pathname values could reach a share through a link in the worktree | Git guard, local git calls | [#8](https://github.com/fneziraslantas-tech/Tezgah/pull/8) |
| 5 | Trust prompt could show a stale command while the current profile with the same Id ran | Trust prompt, `RunService` | [#9](https://github.com/fneziraslantas-tech/Tezgah/pull/9) |
| 6 | Creation-time trust anchor could carry trust to a folder re-created at the same path (tunneling) | Trust anchor | [#9](https://github.com/fneziraslantas-tech/Tezgah/pull/9) |
| 7 | Quadratic multiline detector regexes without a timeout could stall discovery | Project detectors | [#6](https://github.com/fneziraslantas-tech/Tezgah/pull/6) |
| 8 | Stale "cloned by Tezgah" mark survived replacement of an untrusted cloned folder | Catalog, trust prompt | [#9](https://github.com/fneziraslantas-tech/Tezgah/pull/9) |

Two hardening notes from the report were also applied in [#7](https://github.com/fneziraslantas-tech/Tezgah/pull/7)
(uninstall stops only the processes shown in the plan; the free-space dialog warns about permanent deletion).

Rejected: (1) unpushed work on other branches not counted when HEAD is unborn — only the user can create that state and
deletion still needs their confirmation, so it is a reliability issue, not a boundary crossing; (2) trust binding to a
folder identity merged by a concurrent scan — no impact beyond editing the files in place; the remaining root cause was
tracked as lead 5 and fixed in #9.

**What this does not prove.** "0 confirmed" is a property of this run, not proof that no vulnerability exists; the 8
leads were fixed without runtime confirmation; out-of-scope areas and GitHub settings were not assessed; the audit
covers source `c0c4a18` only. To report a vulnerability, see [SECURITY.md](../SECURITY.md).
