# Claude Coden ohje tälle repolle

Paikallinen AI-avustaja Classic Outlookiin. Ohjelma lukee käyttäjän Outlook-laatikot COM-objektimallilla, arvioi viestit paikallisella kielimallilla (LM Studio) ja kokoaa huomiota vaativat viestit WPF-näkymään. Suunnitelma, arkkitehtuuri ja vaiheiden hyväksymisehdot ovat tiedostossa [Outlook_paikallinen_AI_suunnitelma.md](Outlook_paikallinen_AI_suunnitelma.md). Se on suunnittelun ainoa lähde: päivitä sitä, kun ratkaisu muuttuu, äläkä kopioi sen sisältöä tänne.

## Ympäristö

- Ajokone on koti-pc (Windows 11 Home). Ohjelma ajetaan käyttäjänä `kortt` ilman järjestelmänvalvojan oikeuksia, samalla oikeustasolla kuin Outlook.
- Käytössä on C# ja .NET 10 (`net10.0-windows`). Koodi ja tunnisteet kirjoitetaan englanniksi, käyttöliittymän tekstit ja dokumentaatio suomeksi.
- Ohjelma liittyy vain jo käynnissä olevaan Outlookiin. Se ei saa koskaan käynnistää Outlookia.
- Mallipalvelin on LM Studio osoitteessa `http://127.0.0.1:1234`. Käytä vain loopback-osoitteita. Ennen mallin latausta tarkista, ettei Automatic1111 generoi (`http://127.0.0.1:7860/sdapi/v1/progress`) eikä LM Studiossa ole muuta mallia ladattuna. Pura aina vain itse lataamasi instanssi.

## Tietosuoja: ehdottomat säännöt

- Älä tulosta, lokita, tallenna tiedostoon tai commitoi viestien tekstisisältöä. Kansionimet, lukumäärät ja käyttäjän erikseen valitseman testiviestin otsikko ovat sallittuja.
- Kun Claude ajaa komentoja, niiden tuloste päätyy Claude-istuntoon eli ulkoiseen palveluun. Siksi komentorivityökalut tulostavat oikeista viesteistä vain metatietoja ja koostelukuja. Älä lue tietokannasta otsikoita, lähettäjiä, yhteenvetoja tai perusteluja tulosteeseen. Ainoa poikkeus on keinotekoinen testiaineisto (`eval`).
- Älä lähetä viestien sisältöä tai Outlook-tietoja mihinkään ulkoiseen palveluun. Paikallinen LM Studio on ainoa sallittu vastaanottaja.
- Testit ja kokeilut tehdään keinotekoisilla viesteillä. Oikeita viestejä käsitellään vain käyttäjän pyynnöstä.
- Outlookiin saa kirjoittaa vain sovelluksen omia `AI:`-luokkia, ja kehitysvaiheessa vain käyttäjän valitsemaan testiviestiin. Älä merkitse viestejä luetuiksi äläkä siirrä, poista tai lähetä mitään.
- Älä muuta Outlookin, Windowsin tai virustorjunnan suojausasetuksia.

## Rakenne

- `src/EmailAnalyser.Core`: tekstin valmistelu, kehote (`Prompts/`), vastauksen tarkistus, LM Studio -sovitin, eräajo, arviointi ja salattu tietokanta.
- `src/EmailAnalyser.Outlook`: Outlook COM -sovitin omalla STA-säikeellään.
- `src/EmailAnalyser.Cli`: komentorivityökalu `email-analyser`.
- `src/OutlookProbe`: vaiheen 1 kokeiluohjelma.
- `tests/EmailAnalyser.Tests`: xUnit-testit; eivät tarvitse Outlookia eivätkä LM Studiota.
- `eval/synthetic-emails.json`: keinotekoinen testiaineisto odotettuine luokkineen.
- Ajonaikaiset tiedot (`settings.json`, salattu `analyser.db` ja DPAPI-suojattu `database.key`) ovat kansiossa `%LOCALAPPDATA%\EmailAnalyser`. Kehotteen muutos vaihtaa automaattisesti kehoteversion, joka tallentuu jokaiseen arvioon.

## Komennot

```
dotnet build
dotnet test
email-analyser init --stores 1 4 6 7 8 10   # seurattavat tietovarastot
email-analyser scan --per-store 30          # uusimmat viestit jonoon
email-analyser run                          # käsittele jono erinä
email-analyser status | report              # jonon tila ja koosteluvut
email-analyser eval --models google/gemma-4-12b-qat,google/gemma-4-e4b
email-analyser verify-logs | db-check       # lokien ja salauksen tarkistus
dotnet run --project src/OutlookProbe -- stores     # laatikot ja kansiot
dotnet run --project src/OutlookProbe -- selected   # Outlookissa valittu viesti
dotnet run --project src/OutlookProbe -- read-test 1 4 --count 10   # lukutila ja suojatut ominaisuudet, ei sisältöä
dotnet run --project src/OutlookProbe -- category-test "AI: testiviesti" --store 1 --expect-unread   # vain testiviestiin
dotnet run --project src/OutlookProbe -- open 1 "AI: testiviesti"   # avaa viestin viitteen perusteella
```

`email-analyser` on käännetty ohjelma `src/EmailAnalyser.Cli/bin/Debug/net10.0-windows/email-analyser.exe`, tai sen voi ajaa komennolla `dotnet run --project src/EmailAnalyser.Cli --`. Aja se repon juuresta, jotta `eval` löytää testiaineiston. Aja `dotnet build` ennen käännetyn ohjelman käyttöä: `dotnet test` kääntää vain testiprojektin riippuvuudet, joten muuten `email-analyser.exe` voi käyttää vanhaa kehotetta. Tarkista tulosteen kehoteversio. Tietovarastojen numerot ovat `stores`-komennon järjestyksessä ja voivat muuttua, kun profiiliin lisätään tilejä.

## Dokumentointivastuut

Tämä repo omistaa toteutuksen ja sen dokumentaation. Kun toteutus muuttuu, päivitä README ja suunnitelma samassa työssä. Kuvaa uudet prosessit Mermaid-kaaviolla.

Keskitetty dokumentaatiorepo `Browleri/documentation-repo` (paikallisesti `..\documentation-repo`) omistaa infrastruktuurin, arkkitehtuurin, koneiden, palvelujen, operatiivisten menettelyjen ja verkon dokumentaation. Kun tämän repon muutos vaikuttaa olennaisesti käyttöönottoon, arkkitehtuuriin, verkkoon, operointiin, valvontaan tai ylläpitoon, päivitä dokumentaatiorepo samassa työssä. Tällaisia muutoksia ovat esimerkiksi uudet käynnistystavat, mallipalvelimen asetukset, tietojen sijainnit ja varmuuskopiointipäätökset.

- Ennen päivitystä varmista, että dokumentaatiorepo on `main`-haarassa ja synkronoitu etärepon kanssa.
- Noudata sen [AGENTS.md](../documentation-repo/AGENTS.md)-ohjetta: front matter, sivujen sijoittelu ja linkkimuoto.
- Tee muutos omaan haaraan, commitoi, puske ja avaa PR. PR:ssä kerrotaan tarkistetut infrastruktuurifaktat, muutetut sivut ja jäljelle jäävät jatkotoimet.
- Älä kopioi dokumentaatiorepon sisältöä tähän repoon, vaan linkitä siihen.

## Ennen kuin työ on valmis

- `dotnet build` menee läpi ilman varoituksia, ja mahdolliset testit menevät läpi.
- Viestien sisältöä ei päätynyt tulosteisiin, lokeihin tai commiteihin.
- Suunnitelman vaihe ja hyväksymisehdot on päivitetty, jos ne muuttuivat.
- Dokumentaatiorepo on päivitetty, jos muutos koski sen aluetta.
