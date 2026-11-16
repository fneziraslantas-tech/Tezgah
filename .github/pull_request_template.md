## Ne ve neden / What and why

<!-- Kısa açıklama; ilgili issue varsa bağla (Fixes #123). / Short description; link the issue if any (Fixes #123). -->

## Kontrol listesi / Checklist

- [ ] `dotnet build` ve `dotnet test` yeşil / `dotnet build` and `dotnet test` are green
- [ ] Yeni davranış için test eklendi (güvenlikle ilgiliyse düşmanca girdi örneğiyle) / Tests added for new behaviour (with a hostile-input case if security-relevant)
- [ ] Tehdit modeli değiştiyse [SECURITY.md](../SECURITY.md) güncellendi / [SECURITY.md](../SECURITY.md) updated if the threat model changes
- [ ] Kullanıcıyı etkileyen değişiklikler için [CHANGELOG.md](../CHANGELOG.md) `[Unreleased]` güncellendi / [CHANGELOG.md](../CHANGELOG.md) `[Unreleased]` updated for user-facing changes
