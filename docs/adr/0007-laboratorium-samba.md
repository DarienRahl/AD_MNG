# ADR-0007: Laboratorium testowe na Samba AD DC

- Status: przyjęta
- Data: 2026-10-13

## Decyzja

Testy integracyjne warstwy LDAP uruchamiamy na jednorazowym kontrolerze Samba AD DC (`tools/testlab`) z
deterministycznymi danymi imitującymi polską firmę (OU wg działów i lokalizacji, polskie znaki w nazwach, konta
zablokowane, delegacja helpdesk). Ten sam skrypt działa w CI (Ubuntu).

## Konsekwencje

- Testy integracyjne bez dostępu do produkcyjnego AD.
- Samba różni się w szczegółach od Windows AD (np. brak display specifiers 415, brak schematu Exchange) – funkcje
  zależne od tych różnic wymagają dodatkowej weryfikacji na laboratorium Windows Server przed wydaniem.
