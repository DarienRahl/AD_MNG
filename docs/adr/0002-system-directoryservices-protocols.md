# ADR-0002: Dostęp do AD przez System.DirectoryServices.Protocols

- Status: przyjęta
- Data: 2026-10-13

## Kontekst

Dostępne API: ADSI (`System.DirectoryServices`, z którego korzysta ADUC), `System.DirectoryServices.AccountManagement`,
moduł PowerShell ActiveDirectory (ADWS, port 9389) oraz bezpośrednie LDAP przez `System.DirectoryServices.Protocols` (S.DS.P).

## Decyzja

Cała komunikacja z AD przez S.DS.P (LDAP v3): Negotiate (Kerberos) z podpisywaniem i szyfrowaniem lub LDAPS,
asynchroniczne żądania, kontrolki LDAP (stronicowanie, VLV + sortowanie, ASQ, Show Deleted/Recycled,
Permissive Modify, Tree Delete, SD Flags, Change Notification).

## Uzasadnienie

- ADSI buforuje obiekty (property cache), działa synchronicznie i ma narzut COM – jedno ze źródeł „ociężałości” ADUC.
- S.DS.P daje pełną kontrolę nad tym, *które* atrybuty są pobierane (tylko kolumny widoczne na ekranie) i pozwala
  strumieniować wyniki stronami – pierwsze wiersze pojawiają się po kilkudziesięciu ms.
- AccountManagement jest wolne i ograniczone do kilku klas; ADWS wymaga usługi na DC i nie daje kontroli nad LDAP.
- S.DS.P działa też na Linuksie, co umożliwia testy integracyjne z kontrolerem Samba AD w CI.

Prototyp na laboratorium (Samba 4.19 AD DC): bind 16 ms, pierwsza strona 100 użytkowników 65 ms,
428 obiektów w 174 ms, VLV/ASQ/allowedAttributesEffective działają, 20 równoległych zapytań na jednym połączeniu.

## Konsekwencje

- Własna implementacja tego, co ADSI robi „za darmo” (konwersje typów wg schematu, ranged retrieval, mapowanie błędów).
- Brak zależności od RSAT na stacji administratora.
