# Paikallinen AI-avustaja Classic Outlookiin

Toteutussuunnitelma, 17.11.2026 (päivitetty samana päivänä infrastruktuurikatselmoinnin jälkeen)

## Tavoite ja päätökset

Windows-käyttäjäistunnossa toimiva sovellus seuraa käyttäjän valitsemia Classic Outlookin postilaatikoita. Tilit ovat pääosin M365-tilejä, mukana yksi Gmail-tili. Sovellus lukee viestit nykyisen Outlook-profiilin kautta, arvioi ne paikallisella kielimallilla ja kokoaa huomiota vaativat viestit yhteen näkymään. M365-viesteille voidaan lisätä Outlook-luokkia.

Sovellus ei kerää salasanoja eikä kopioi Outlookin kirjautumistunnisteita. Se käyttää Outlookin objektimallia käyttäjän olemassa olevassa istunnossa. Outlook vastaa tilien kirjautumisesta ja synkronoinnista. Erillistä Graph-sovellusrekisteröintiä ei tässä toteutuksessa tarvita. Organisaation paikalliset käyttö- ja tietojenkäsittelyrajoitukset sekä Outlookin ohjelmallisen käytön suojausasetukset voivat silti vaikuttaa toimintaan.

Suositus: C#/.NET 10, Outlook COM -sovitin, salattu SQLite ja LM Studio paikallisena mallipalvelimena. Mallisovitin käyttää OpenAI-yhteensopivaa rajapintaa, joten Ollama voidaan myöhemmin liittää vaihtoehtoisena palvelimena. Malli ladataan vain eräajon ajaksi eikä sitä pidetä muistissa. Ensimmäinen käyttöliittymä on yksinkertainen Windowsin WPF-sovellus; erillistä React-palvelinta ei tarvita.

## Nykyinen ympäristö

Sovellus ajetaan koneella [koti-pc](../documentation-repo/hosts/koti-pc.md). Alla olevat tiedot on tarkistettu dokumentaatiosta ja koneelta 17.11.2026.

| Asia | Tila | Vaikutus suunnitelmaan |
| --- | --- | --- |
| Käyttöjärjestelmä | Windows 11 Home, build 26300 | Ei BitLockeria eikä ryhmäkäytäntöeditoria |
| Outlook | Classic Outlook x64, Click-to-Run 16.0.20527, Microsoft 365 -kuluttajalisenssi | COM-objektimalli käytettävissä; koneella ei ole Outlookin suojauskäytäntöjä |
| Virustorjunta | Microsoft Defender päällä ja ajan tasalla | Ohjelmallisen käytön suojauksen ei pitäisi kysyä lupaa; varmistetaan vaiheessa 1 |
| Outlookin välimuisti | 6 OST-tiedostoa, yhteensä 7,4 Gt, salaamattomalla levyllä | Ks. Salaus |
| Levyn salaus | Ei käytössä asemilla C: eikä E: | Sovelluksen tietokanta salataan itse |
| Laitteisto | Ryzen 7 3700X, 32 Gt RAM, RTX 2070 SUPER 8 Gt | Yksi malliajo kerrallaan, näytönohjain jaetaan Automatic1111:n kanssa |
| .NET | .NET 10 SDK 10.0.404 ja ajoympäristöt 10.0.15 asennettu; vanha .NET 6 -ajoympäristö jäljellä | WPF-, konsoli- ja xUnit-pohjat käytettävissä |
| LM Studio | Käynnistyy käyttäjän `kortt` kirjautuessa, kuuntelee `0.0.0.0:1234` ilman tunnistautumista. Kehotteiden lokitus (`logSensitiveData`) ja `verbose` kytketty pois 17.11.2026 | Poiskytkentä todettu testiviestillä; tarkistetaan uudelleen LM Studion päivitysten jälkeen |
| LM Studion mallit | Mm. Gemma 4 12B QAT, Gemma 4 E4B, Qwen 3.5 9B; suomenkielinen vertailu 2.10.2026 | Mallit ja vertailutyökalu ovat valmiina |
| Ollama | Kuuntelee kaikissa osoitteissa (`OLLAMA_HOST=0.0.0.0:11434`), vain `llama3.1:8b` | Ei käytetä ensimmäisessä versiossa |
| Muut GPU-käyttäjät | Automatic1111 + A1111 Prompter (tuotanto); Prompterin kehoteavustaja käyttää LM Studiota | Eräajo väistää kuvagenerointia ja Prompterin mallia |
| Tilit | Interaktiivinen käyttäjä `kortt`; Prompter ja GitHub-runner ajetaan tilillä `Martti-local` | Sovellus ajetaan käyttäjänä `kortt` |
| Varmuuskopiointi | Käyttäjän `AppData\Local` ei kuulu restic-varmuuskopioihin | Ks. Säilytys ja varmuuskopiointi |

## Paikallisuuden rajaus

- Viestien analyysi tapahtuu omalla koneella ladatulla mallilla.
- Arviot ja käsittelytila tallennetaan salattuun tietokantaan käyttäjän paikalliseen AppData-hakemistoon, ei OneDrive-kansioon.
- Tavallinen sähköpostisynkronointi tapahtuu edelleen Microsoftin ja Googlen palveluihin.
- Outlookiin lisätyt luokat synkronoituvat M365-postilaatikkoon. Jos myös AI:n tuottamien merkintöjen pitää pysyä kokonaan paikallisina, valitaan pelkkä paikallinen näkymä.
- Gmailin IMAP-viesteille käytetään paikallisia luokituksia: Outlookin väriluokkia ei oleteta tuetuiksi eikä niitä muuteta Gmail-tunnisteiksi.
- Pilvimallia ei käytetä varavaihtoehtona. Mallipalvelimen puuttuessa työ jää jonoon.
- Sovelluksen mallisovitin hyväksyy vain loopback-osoitteet (`127.0.0.1`, `::1`) eikä seuraa uudelleenohjauksia tai välityspalvelimia.
- LM Studion palvelin on avoinna lähiverkkoon, koska Ullis käyttää sitä. Viestien sisältö ei näy muille rajapinnan kautta, joten avoimuus koskee vain mallin käyttöoikeutta. Sisällön paikallisuus edellyttää, että LM Studion kehotteiden lokitus on pois päältä. Tämä tarkistetaan ennen jokaista eräajoa, jos asetus on luettavissa, ja muuten käyttöönoton tarkistuslistalla.

## Käyttökokemus

Ensimmäisellä käynnistyskerralla sovellus näyttää Outlook-profiilissa näkyvät tietovarastot ja kansiot. Käyttäjä valitsee seurattavat laatikot ja kansiot sekä mallin. Jaetut laatikot otetaan mukaan vain siltä osin kuin ne ovat Outlookissa käytettävissä nykyisillä oikeuksilla.

Alkuun tarjotaan 100–200 viimeaikaisen viestin kokeilu. Luokitukset näkyvät ensin vain sovelluksessa. Käyttäjä voi korjata niitä ja ottaa myöhemmin käyttöön Outlook-luokkien lisäämisen.

Päänäkymässä on kaikkien valittujen laatikoiden yhteinen lista: laatikko, lähettäjä, otsikko, arvioitu toimenpide, kiireellisyys, mahdollinen määräaika ja lyhyt yhteenveto. Viestin voi avata Outlookiin. Saatavilla ovat suodattimet Vastattava, Tehtävä, Odottaa muita ja Tarkista sekä luokituksen korjaus. Näkyvissä ovat myös viimeisen onnistuneen tarkistuksen aika, jonon koko, seuraavan eräajon arvioitu aika tai sen lykkäyksen syy sekä laatikkokohtaiset virheet. Painike "Käsittele nyt" käynnistää eräajon heti. Sovelluksen voi keskeyttää.

## Arkkitehtuuri

| Osa | Vastuu |
| --- | --- |
| Outlook-sovitin | Laatikoiden ja kansioiden luettelo, viestien luku, avaaminen Outlookissa ja rajatut luokkamuutokset |
| Tarkistusajastin | Kevyt COM-tarkistus kahden minuutin välein; uudet viestit jonoon ilman malliajoa |
| Eräajon ohjaus | Erän käynnistysehdot, GPU-tilanteen tarkistus, mallin lataus ja purku, uudelleenyritykset |
| Sisällön valmistelu | Pelkkä teksti, pituusrajat, turvallinen katkaisu ja tarvittava keskustelukonteksti |
| Mallisovitin | OpenAI-yhteensopiva chat-rajapinta (LM Studio), mallin lataus ja purku LM Studion REST-rajapinnalla, aikakatkaisu ja JSON Schema -vastauksen tarkistus |
| Säännöt ja toimintalogiikka | Sallitut luokat, käyttäjän korjausten etusija ja Outlookiin kirjoittamisen ehdot |
| Salattu SQLite | Viitteet, käsittelytila, arviot, käyttäjän korjaukset ja muutoshistoria |
| WPF-käyttöliittymä | Yhteinen viestilista, tarkastaminen ja asetukset |

Outlook COM -kutsut hoidetaan omalla STA-säikeellä ja tarvittavalla viestipumpulla. COM-olioita ei siirretä mallityöntekijöille: niille annetaan tavallisia tekstimuotoisia tietoja. Mallin odotus ei saa pysäyttää käyttöliittymää tai Outlook-käsittelyä. Viestit luetaan pienissä erissä ja COM-viittaukset vapautetaan hallitusti.

### Outlook COM -toteutuksen yksityiskohdat

- Sovellus liittyy käynnissä olevaan Outlookiin. .NET 5:stä alkaen `Marshal.GetActiveObject` puuttuu, ja `new Application()` käynnistäisi suljetun Outlookin. Siksi ensin tarkistetaan `OUTLOOK.EXE`-prosessi ja liitytään vasta sen jälkeen. Outlook toimii omassa prosessissaan, joten sovelluksen bittisyydellä ei ole väliä.
- Sovellus ja Outlook ajetaan samalla oikeustasolla ilman järjestelmänvalvojan oikeuksia. Oikeustasojen ero estää COM-yhteyden.
- Outlookin interop-tyypit (NuGet `Microsoft.Office.Interop.Outlook`) upotetaan ohjelmaan (`EmbedInteropTypes`). Muuten .NET 10 yrittää ladata ajon aikana `office.dll`-kokoonpanon, eikä löydä sitä.
- Kansion viestit luetellaan `Folder.GetTable()`-taululla. Sarakkeiksi haetaan EntryID, ReceivedTime, LastModificationTime ja `PR_INTERNET_MESSAGE_ID`, jolloin kaikkia viestejä ei tarvitse avata. Aikasuodatus tehdään DASL-kyselynä (`urn:schemas:httpmail:datereceived`), koska `Restrict` riippuu alueasetusten päivämäärämuodosta.
- `Body` ja `SenderEmailAddress` kuuluvat ohjelmallisen käytön suojauksen piiriin. Niiden toiminta ilman kehotteita on vaiheen 1 hyväksymisehto.
- IRM- tai OME-salatun viestin tekstiä ei välttämättä voi lukea. Tällainen viesti merkitään tilaan Tarkista eikä sitä luokitella sisällön perusteella.
- Gmailista seurataan vain Saapuneet-kansiota ja käyttäjän valitsemia kansioita, ei kansiota `[Gmail]/Kaikki viestit`. Jos IMAP-tili lataa vain otsikot, tekstiä ei ole saatavilla ja viesti merkitään tilaan Tarkista.
- Luokan lisääminen muuttaa LastModificationTime-arvoa. Uusi malliajo tehdään vain, jos sisältötiiviste muuttuu.

## Viestien löytäminen ja käsittely

1. Alussa luetellaan kaikki valitut tietovarastot; pelkkä profiilin oletus-Inbox ei riitä. Mukaan voidaan valita myös kansiot, joihin Outlook-säännöt siirtävät saapuneita viestejä.
2. Tarkistus tehdään oletuksena kahden minuutin välein. Se lukee vain Outlookia ja lisää uudet viestit jonoon; mallia ei kutsuta. Käynnistyessä ja lepotilasta palattaessa tehdään kiinniottotarkistus.
3. Kansiokohtainen tarkistuspiste ja päällekkäinen aikaikkuna löytävät viiveellä näkyvät viestit. Lisäksi tehdään rajattu viime päivien täsmäytys. Näitä verrataan tietokannan käsittelytilaan; koko laatikkoa ei lueta joka kierroksella.
4. Viestistä luetaan lähettäjä, vastaanottajat, otsikko, vastaanottoaika ja tekstisisältö. Viestiä ei merkitä luetuksi. Muiden Outlook-kohteiden kuin sähköpostien käsittely ohitetaan alussa.
5. Sääntöosuma voi ohittaa mallin vain selkeissä käyttäjän määrittelemissä tapauksissa, kuten tietyn uutiskirjeen kohdalla. Sääntöosumat käsitellään heti, eräajoa odottamatta.
6. Jonossa olevat viestit käsitellään eräajona (ks. seuraava luku). Viestin teksti luetaan Outlookista uudelleen eräajon aikana, joten raakatekstiä ei säilytetä jonossa.
7. Malli tuottaa rakenteisen arvion, joka tarkistetaan ohjelmallisesti. Virheellinen vastaus voidaan yrittää kerran uudelleen; jatkuva virhe näkyy tarkistettavana eikä onnistuneena luokituksena.
8. Tulos tallennetaan ja mahdolliset sallitut Outlook-luokat lisätään erillisessä kirjoitusvaiheessa.

Tunnistuksessa säilytetään StoreID ja EntryID avaamista varten, mutta niitä ei käsitellä ikuisesti pysyvinä tunnisteina. Siirrot ja kopiot huomioidaan laatikkokohtaisilla lisätiedoilla, kuten Internet Message-ID:llä ja sisältötiivisteellä. Message-ID voi puuttua tai esiintyä kopioissa: yksinään se ei riitä. Epäselvässä tapauksessa vältetään kirjoittamista väärään viestiin. Pelkkä AI-luokan lisäämisen aiheuttama muutos ei saa käynnistää uutta malliajoa.

Ensimmäinen versio arvioi uuden viestin, ei lupaa täydellistä keskustelun tehtävätilaa. Odottaa muita -tilan ja vastattu/hoidettu-päätelmien luotettava ylläpito edellyttää myöhemmin myös lähetettyjen viestien ja ketjujen seurantaa.

## Eräkäsittely ja GPU:n jakaminen

Mallia ei pidetä muistissa. Mallin lataaminen vie LM Studion mittauksissa noin 36–80 sekuntia, joten viestejä ei käsitellä yksi kerrallaan heti saapuessaan, vaan ne kootaan eriksi.

**Erän käynnistys.** Eräajo alkaa, kun jokin seuraavista toteutuu:
- jonossa on vähintään 10 viestiä,
- vanhin jonossa oleva viesti on odottanut 20 minuuttia,
- käyttäjä painaa "Käsittele nyt".

Raja-arvot ovat säädettäviä. Oletuksilla kiireellinen viesti näkyy arvioituna viimeistään noin 20–25 minuutin kuluttua saapumisesta. Raja-arvot säädetään vaiheessa 4 käytännön kokemuksen mukaan.

**Ennen latausta tarkistetaan:**
1. Automatic1111 ei generoi kuvaa. Tämä luetaan rajapinnasta `http://127.0.0.1:7860/sdapi/v1/progress`; jos rajapinta ei vastaa, Automatic1111:n katsotaan olevan vapaana.
2. LM Studiossa ei ole ladattuna muuta mallia, kuten Prompterin kehoteavustajan mallia. Kaksi mallia ei mahdu yhtä aikaa 8 Gt:n näytönohjaimeen.

Jos jompikumpi ehto ei täyty, erää lykätään ja yritetään uudelleen muutaman minuutin kuluttua. Lykkäyksen syy näytetään käyttöliittymässä. Jos erää on lykätty yli tunnin, lykkäys näytetään varoituksena; kuvagenerointia ei kuitenkaan keskeytetä.

**Erän aikana:**
- Sovellus lataa mallin LM Studion REST-rajapinnalla (`POST /api/v1/models/load`) valitulla kontekstipituudella (aluksi 8192) ja ottaa talteen ladatun instanssin tunnisteen. Tunniste on ensimmäisellä latauksella sama kuin mallin nimi, joten sovellus pitää instanssia omanaan vain, jos samaa mallia ei ollut ladattuna ennen latausta.
- Viestit käsitellään yksi kerrallaan. Ennen jokaista viestiä tarkistetaan Automatic1111:n tila. Jos generointi alkaa, erä keskeytetään hallitusti ja jäljellä olevat viestit jäävät jonoon.
- Yhden erän enimmäiskesto on oletuksena 15 minuuttia. Pitkä kiinniotto, kuten loman jälkeen kertynyt jono, käsitellään useana eränä, joiden välillä näytönohjain vapautetaan.

**Erän jälkeen** sovellus purkaa vain itse lataamansa instanssin. Purku tehdään myös virhetilanteissa ja sovellusta suljettaessa. Varmuuden vuoksi latauspyynnössä asetetaan lyhyt käyttämättömyysaika (TTL), jolloin LM Studio purkaa mallin itse, vaikka sovellus kaatuisi.

**Arvioitu nopeus** on noin 15–20 s viestiä kohden Gemma 4 12B -mallilla ja 6–8 s Gemma 4 E4B -mallilla, kun lataus ei ole mukana. 100–200 viestin kokeilu kestää siis noin 25–60 minuuttia. Arviot mitataan vaiheessa 2.

## Luokittelumalli

| Kenttä | Esimerkkejä / käytäntö |
| --- | --- |
| Aihe | Työ, sijoitukset, hallinto, henkilökohtainen, uutiskirje, muu; käyttäjän muokattavissa |
| Toimenpide | Vastattava, tehtävä, tiedoksi, tarkista; myöhemmin odottaa muita |
| Kiireellisyys | Tänään, tällä viikolla, myöhemmin, ei määräaikaa |
| Määräaika | Vain viestistä perusteltava päivämäärä; muuten null |
| Yhteenveto | 1–2 virkettä suomeksi |
| Perustelu | Lyhyt selitys ja tarvittaessa viestin tekstikohta |
| Tarkistettava | Puuttuva konteksti, epäselvä pyyntö, katkaistu sisältö tai ristiriita |

Määräaika ja kiireellisyys pidetään erillään. Mallille annetaan nykyinen aika Europe/Helsinki-aikavyöhykkeellä ja viestin päiväys. Mallin omaa numeerista varmuutta ei käytetä sellaisenaan automaation turvallisuusrajana.

Outlookissa käytetään aluksi vain toimintaluokkia AI: Vastattava, AI: Tehtävä, AI: Tiedoksi ja AI: Tarkista. Sovellus muuttaa vain omia nimettyjä luokkiaan ja säilyttää muut. Luokkien erotin käsitellään Outlookin alueasetusten mukaan. Nykyiset luokat luetaan uudelleen juuri ennen tallennusta. Käyttäjän sovelluksessa tekemä korjaus lukitsee kyseisen arvion automaattiselta ylikirjoitukselta, kunnes käyttäjä pyytää uutta arviota.

## Malli ja mallipalvelin

LM Studio on päämallipalvelin, koska:
- Ehdokasmallit ovat jo asennettuina ja niiden suomenkielinen suorituskyky on mitattu tällä koneella.
- Yksi mallipalvelin hallitsee näytönohjaimen muistia. Erillinen Ollama latautuisi Prompterin LM Studio -mallin rinnalle, eikä kumpikaan palvelin tietäisi toisesta.
- JSON Schema -rajoitettu vastaus ja ajattelun poiskytkentä (`reasoning_effort: "none"`) on jo todettu toimiviksi LM Studion vertailussa.
- LM Studio käynnistyy käyttäjän `kortt` kirjautuessa, samassa istunnossa kuin Outlook ja tämä sovellus.

Ehdokasmallit:

| Malli | Peruste | Muistinkulutus |
| --- | --- | --- |
| Gemma 4 12B QAT (`google/gemma-4-12b-qat`) | Paras tulos suomenkielisissä tehtävissä (100 % opiskelu- ja hakutehtävissä), 14 tok/s | 6,9 Gt GPU / 3,4 Gt RAM |
| Gemma 4 E4B (`google/gemma-4-e4b`) | Kolme kertaa nopeampi, hieman heikommat tulokset, 43 tok/s | 4,2 Gt GPU / 3,6 Gt RAM |

Qwen 3.5 9B jätetään pois, koska se epäonnistui vertailun hakutehtävässä. Gemma 4 26B ei sovi, koska se vie 9 Gt keskusmuistia Automatic1111:n rinnalla. Malli valitaan suomen- ja englanninkielisen oman arviointiaineiston perusteella. Vertailuun laajennetaan olemassa olevaa työkalua `C:\Apps\llm-bench` sähköpostin luokittelutehtävällä. Siinä käytetään ensin keinotekoisia viestejä ja vasta sen jälkeen käyttäjän merkitsemää otosta. Ajattelu kytketään pois, koska ajattelevat mallit kuluttivat vertailussa koko vastausbudjettinsa. Lämpötila on 0. Yksi malliajo kerrallaan; nopeus ja muistinkulutus mitataan oikealla koneella.

Ennen kuin sähköpostia lähetetään LM Studiolle:
- Kehotteiden lokitus (`logSensitiveData`) kytketään pois ja poiskytkentä varmistetaan lähettämällä keinotekoinen testiviesti ja tarkistamalla kansio `C:\Users\kortt\.lmstudio\server-logs`. Asetus on palvelinkohtainen, joten se koskee myös Prompterin kehoteavustajaa.
- Ei web-hakua, ulkoisia työkaluja eikä automaattista mallilatausta verkosta viestin käsittelyn aikana. Sovellus lataa vain valmiiksi asennetun mallin.

Ollama-sovitin voidaan lisätä myöhemmin samalla OpenAI-yhteensopivalla rajapinnalla. Silloin Ollama sidotaan osoitteeseen `127.0.0.1`, sen pilviominaisuudet poistetaan käytöstä (`OLLAMA_NO_CLOUD=1`) ja LAN-palomuurisäännöt poistetaan. Ollamaa ja LM Studiota ei käytetä samanaikaisesti.

Viestisisältö on epäluotettua aineistoa: sen sisältämät ohjeet eivät saa muuttaa järjestelmän toimintaa. Malli antaa vain arvion; se ei saa COM-pääsyä, tiedostojärjestelmätyökaluja tai sähköpostin lähetysoikeutta sovelluksen kautta. Sallitut toiminnot ja kohteet valitsee ohjelmakoodi.

## Tietojen käsittely

Raakaviestiteksti käsitellään oletuksena muistissa eikä tallenneta pysyvään tietokantaan. Tietokantaan jää kuitenkin arkaluonteisia tietoja: lähettäjät, otsikot, yhteenvedot ja perustelut. Lokeihin ei kirjoiteta sisältöä tai henkilötietoja oletuksena. Liitteitä ja ulkoisia kuvia ei avata ensimmäisessä versiossa.

### Salaus

Koneen levyjä ei ole salattu, joten tietokanta salataan sovelluksessa.

**Ratkaisu:** koko tietokantatiedosto salataan SQLCipher-yhteensopivalla salauksella. Toteutukseksi käy esimerkiksi SQLite3 Multiple Ciphers, jota käytetään Microsoft.Data.Sqlite-kirjaston kanssa. Kirjaston NuGet-paketti ja ylläpitotila tarkistetaan toteutusvaiheessa; Zeteticin virallinen SQLCipher .NET:lle on maksullinen. Ensimmäisellä käynnistyksellä luodaan satunnainen 256-bittinen avain. Avain tallennetaan AppData-hakemistoon Windowsin DPAPI:lla käyttäjäkohtaisesti suojattuna (`ProtectedData`, `CurrentUser`).

**Miksi ei kenttäkohtaista DPAPI-salausta:**
- Kenttäkohtainen salaus jättäisi selväkieliseksi kaiken, mitä ei erikseen salata: lähettäjät, otsikot, ajat, luokat ja Outlook-tunnisteet. Myös otsikko ja lähettäjä ovat arkaluonteisia.
- Jokainen uusi kenttä pitäisi muistaa salata, ja virhe jäisi helposti huomaamatta.
- Salattuja kenttiä ei voi indeksoida, suodattaa eikä hakea SQL:llä.
- Koko tiedoston salaus kattaa myös indeksit, väliaikaistiedostot sekä WAL- ja journal-tiedostot, joihin vanhoja arvoja voi jäädä.
- Avainten hallinta on molemmissa sama: DPAPI. Koko tiedoston salaus vaatii vain yhden lisäkirjaston.

**Rajaukset:**
- Salaus suojaa levyn, levykuvan tai tiedostokopion lukemiselta toisella koneella tai toisella tilillä. Se ei suojaa käyttäjän `kortt` istunnossa ajettavalta haittaohjelmalta, joka voi purkaa DPAPI-avaimen samalla tavalla kuin sovellus.
- DPAPI-avain on sidottu käyttäjän Windows-tunnistautumiseen. Tavallinen salasanan vaihto säilyttää avaimen, mutta ylläpitäjän tekemä salasanan nollaus hävittää sen. Silloin tietokanta luodaan uudelleen. Käyttäjän korjaukset voi siksi viedä erikseen tiedostoon (ks. Säilytys ja varmuuskopiointi).
- Outlookin omat OST-välimuistit (6 tiedostoa, 7,4 Gt) ovat samalla levyllä salaamattomina ja sisältävät kaikki viestit kokonaan. Sovelluksen salaus estää siis vain sen, että tämä sovellus lisäisi uuden salaamattoman kopion. Koko koneen suojaamiseen tarvitaan levyn salaus (Windows 11 Pro ja BitLocker). Siitä päätetään erikseen, eikä se ole tämän toteutuksen edellytys.

### Säilytys ja varmuuskopiointi

Säilytysaika on säädettävä, oletus 90 päivää; käyttäjä voi poistaa paikalliset arviot. Tietokantahakemisto on käyttäjäkohtainen ja suojattu Windows-käyttöoikeuksilla.

Käyttäjän `AppData\Local` ei kuulu koneen restic-varmuuskopioihin. Arviot ovat uudelleen tuotettavissa, mutta käyttäjän korjaukset ja merkitty arviointiotos eivät ole. Ensimmäisessä versiossa tietokantaa ei varmuuskopioida. Sovellus tarjoaa korjausten ja arviointiotoksen viennin salattuun tiedostoon. Jos varmuuskopiointi myöhemmin lisätään, noudatetaan Kertaus-palvelun käytäntöä: kopio pidetään lähiverkon varmuuskopiotasoilla, eikä sitä viedä tasoille 2 ja 3 (pilvikopio) ilman erillistä päätöstä.

## Toteutusvaiheet ja hyväksymisehdot

### 0. Valmistelu

Vaihe 0 tehtiin 17.11.2026:

- .NET 10 SDK 10.0.404 on asennettu (`winget install --id Microsoft.DotNet.SDK.10 -e`).
- LM Studion kehotteiden lokitus on kytketty pois: `logSensitiveData` ja `verbose` asetettu arvoon `false` ja palvelin käynnistetty uudelleen. Testiviestin tunnistetta ei löytynyt kansiosta `server-logs`. Vanhat palvelinlokit on poistettu.
- LM Studion REST-rajapinta on kokeiltu Gemma 4 12B QAT -mallilla keinotekoisella viestillä, kun Automatic1111 oli vapaana:
  - `GET /api/v1/models` näyttää ladatut instanssit, joten erän esiehto voidaan tarkistaa.
  - `POST /api/v1/models/load` latasi mallin 80 sekunnissa. Tämä oli ensimmäinen lataus E:-levyltä, ja lataus kestää aiempia mittauksia (36 s) pidempään.
  - `/v1/chat/completions` palautti strict JSON Schema -muotoisen arvion 12 sekunnissa (145 syöte- ja 110 vastaustokenia, `finish_reason: stop`) ajattelu pois kytkettynä. Arvio oli järkevä: vastattava, määräaika 18.11.
  - `POST /api/v1/models/unload` purki instanssin sekunnissa, minkä jälkeen yhtään mallia ei ollut ladattuna.
  - Näytönohjaimen muistia oli käytössä 3 491 MiB ennen latausta, 7 778 MiB mallin ollessa ladattuna ja 1 793 MiB purun jälkeen. 12B-malli mahtuu siis 8 Gt:n näytönohjaimeen vain, kun Automatic1111 on vapaana.
  - Vastaus oli sisennettyä JSONia, joten tyhjätilaa kului tokeneina. Kehotteessa voidaan pyytää tiivis JSON.

### 1. Outlook-yhteyden kokeilu — ensimmäinen toteutettava työ

Pieni C#-konsoliohjelma luettelee laatikot ja valitut kansiot, lukee kustakin esimerkiksi kymmenen uusinta sähköpostia `GetTable`-taululla sekä avaa viitteen perusteella viestin Outlookissa. Sisältöä ei lähetetä mallille eikä tallenneta lokiin. Yhdelle erikseen valitulle M365-testiviestille kokeillaan AI-luokan lisäystä ja poistoa. Ohjelma liittyy vain käynnissä olevaan Outlookiin.

Hyväksytty, kun:
- kaikki tarkoitetut M365-laatikot ja Gmail ovat luettavissa,
- `Body` ja `SenderEmailAddress` voidaan lukea ilman suojauskehotteita,
- muiden luokkien ja lukutilan säilyminen on todettu,
- ohjelma ei käynnistä Outlookia, jos se on suljettu,
- suojauskehotteiden ja mahdollisten organisaatiorajoitusten vaikutus tunnetaan; jos suojausasetus estää käytön, sitä ei poisteta automaattisesti.

### 2. Paikallinen arviointi

Toteutetaan salattu SQLite-jono, tekstin valmistelu, eräajon ohjaus, LM Studio -sovitin ja vastauksen tarkistus. Mallivertailu tehdään ensin työkalulla `llm-bench` keinotekoisilla viesteillä. Sen jälkeen ajetaan 100–200 oikean viestin otos ilman Outlook-muutoksia. Mallin versio, kehote ja asetukset tallennetaan arvioihin.

Hyväksytty, kun:
- vastaukset noudattavat skeemaa ja katkaistu sisältö näkyy,
- mallin puuttuminen jättää työt jonoon,
- kaikki mallikutsut menevät loopback-osoitteeseen ja LM Studion lokeihin ei päädy viestisisältöä,
- malli puretaan jokaisen erän jälkeen myös virhetilanteissa,
- erä lykkääntyy, kun Automatic1111 generoi tai LM Studiossa on muu malli ladattuna,
- tietokantatiedosto ei aukea ilman avainta,
- käsittelynopeus ja muistinkulutus on mitattu.

### 3. Yhteinen näkymä ja laadun arviointi

Rakennetaan WPF-lista, suodatus, avaaminen Outlookissa ja käyttäjän korjaukset. Käyttäjä merkitsee otoksen viestit itse. Kehotetta säädetään vain osalla aineistosta ja tulos arvioidaan erillisellä osalla.

Mitataan erityisesti huomiota vaativien viestien löytyminen ja virheelliset hälytykset. Alustava tavoite on vähintään 95 %:n löytymisosuus arviointiotoksen toimenpiteitä vaativille viesteille, mutta tulos raportoidaan myös lukumäärinä; pieni otos ei takaa vastaavaa laatua jatkossa. Jos aineistossa on liian vähän tällaisia viestejä, otosta kasvatetaan. Ennen luokkien automaatiota käydään läpi jokainen huomiotta jäänyt viesti ja asetetaan hyväksyttävä hälytystaso käytännön kokemuksen mukaan.

### 4. Jatkuva seuranta ja luokat

Lisätään kahden minuutin tarkistus, eräajon käynnistysehdot, keskeytys/jatkaminen, kiinniotto, rajatut uudelleenyritykset ja valinnainen M365-luokkien kirjoitus. Gmail näkyy samassa listassa paikallisin luokin. Luokkamuutoksista säilytetään palautukseen tarvittava historia; palautus poistaa vain sovelluksen omat muutokset huomioiden myöhemmät käyttäjämuutokset.

Hyväksytty, kun lepotila, Outlookin sulkeminen ja uudelleenkäynnistys, verkkokatko, LM Studion uudelleenkäynnistys, kansiosiirto ja vain luku -kansio eivät hukkaa käsittelytilaa tai aiheuta toistuvia muutoksia. Viestien avaus tai arviointi ei merkitse niitä luetuiksi. Kuvagenerointi ei hidastu sovelluksen takia. Käyttöönoton jälkeen seurataan noin viikon ajan käytännön toimivuutta, eräviivettä ja lykkäysten määrää ja säädetään erän raja-arvot.

### 5. Käyttöönotto

Paketoidaan käyttäjäkohtainen Windows-sovellus ja ohjeet. Sovellus ajetaan käyttäjänä `kortt` ilman järjestelmänvalvojan oikeuksia. Käynnistys kirjautuessa on valinnainen ja toimii vain kirjautuneen käyttäjän istunnossa. Sovellusta ei ajeta tilillä `Martti-local`, ei käynnistetä järjestelmän käynnistyessä, ei asenneta Windows-palveluksi eikä asenneta GitHub Actions -runnerin kautta. Ohjelma odottaa Outlookia, jos se ei ole käynnissä; suljettua Outlookia ei avata huomaamatta. Jos LM Studio ei vastaa, työt jäävät jonoon ja tila näytetään käyttöliittymässä. Asennustapa ja Office-yhteensopivuus varmistetaan oikealla koneella.

Käyttöönoton yhteydessä sovellus dokumentoidaan documentation-repoon sen AGENTS.md-ohjeiden mukaan: ensin projektisivu `projects/`-kansioon ja vakiintuneena palvelusivu. Samalla päivitetään koti-pc- ja LM Studio -sivujen kuluttajatiedot sekä varmuuskopiointipäätös.

## Ensiversion ulkopuolelle jäävät asiat

Liitteiden analyysi, OCR, automaattinen lähetys, poistaminen ja kansiosiirrot sekä täydellinen tehtävien valmistumisen päättely. Ollama-sovitin, tietokannan varmuuskopiointi ja levyn salaus päätetään erikseen. Myöhempiä laajennuksia ovat lähetettyjen viestien seuranta, keskustelukonteksti, paikallinen haku ja aamukooste. Niiden hyödyllisyys arvioidaan perusluokittelun jälkeen.

## Riippuvuudet ja rajoitukset

Tämä on suunnitelma, ei vielä käyttäjän Outlook-ympäristössä testattu toteutus. Tarvitaan Windows, Classic Outlook, toimiva profiili, .NET 10 ja LM Studio, jossa on vähintään yksi ehdokasmalli. Työ tehdään käyttäjän oikeuksilla; organisaatiokäytännöt voivat silti estää sovelluksen ajamisen tai Outlook-automaation. Gmailista näkyy Outlookiin synkronoitu sisältö ja Gmailin kansiorakenteen aiheuttamat kopiot pitää huomioida. Käyttäjän pitää valita sääntöjen siirtokansiot seurantaan, jos niiden viestejä halutaan arvioida.

Näytönohjain on yhteinen Automatic1111:n, Prompterin kehoteavustajan ja tämän sovelluksen kesken. Eräajo väistää muita käyttäjiä, joten kuormitetulla hetkellä arviot viivästyvät. LM Studion palvelin on avoinna lähiverkkoon ilman tunnistautumista. Tunnistautumisen käyttöönotosta päätetään erikseen, koska se vaatisi muutoksia myös Prompteriin.

## Lähteet

- Microsoft: Useita tilejä samassa Outlook-profiilissa: https://learn.microsoft.com/en-us/office/vba/outlook/Concepts/Accounts/using-multiple-accounts-for-the-same-profile-on-outlook
- Microsoft: Store.GetDefaultFolder: https://learn.microsoft.com/en-us/office/vba/api/Outlook.Store.GetDefaultFolder
- Microsoft: Folder.GetTable: https://learn.microsoft.com/en-us/office/vba/api/outlook.folder.gettable
- Microsoft: EntryID ja StoreID: https://learn.microsoft.com/en-us/office/vba/outlook/how-to/items-folders-and-stores/working-with-entryids-and-storeids
- Microsoft: MailItem.Categories: https://learn.microsoft.com/en-us/office/vba/api/outlook.mailitem.categories
- Microsoft: Outlook-objektimallin suojaus: https://learn.microsoft.com/en-us/office/vba/outlook/how-to/security/security-behavior-of-the-outlook-object-model
- Microsoft: Objektimalli ei sovellu Windows-palveluun: https://learn.microsoft.com/en-us/microsoft-365-apps/outlook/user-interface/oom-unsuitable-in-windows-service
- Microsoft: Outlook-luokat ja IMAP-rajoitus: https://support.microsoft.com/en-us/outlook/mail/create-and-assign-color-categories-in-outlook
- Microsoft: DPAPI ja ProtectedData: https://learn.microsoft.com/en-us/dotnet/standard/security/how-to-use-data-protection
- Microsoft: Microsoft.Data.Sqlite ja salaus: https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/encryption
- SQLite3 Multiple Ciphers: https://utelle.github.io/SQLite3MultipleCiphers/
- LM Studio: mallin lataus: https://lmstudio.ai/docs/developer/rest/load
- LM Studio: mallin purku: https://lmstudio.ai/docs/developer/rest/unload
- LM Studio: Chat Completions: https://lmstudio.ai/docs/developer/openai-compat/chat-completions
- LM Studio: offline-käyttö: https://lmstudio.ai/docs/app/offline
- Ollama: paikallisuus ja pilviominaisuuksien poistaminen: https://github.com/ollama/ollama/blob/main/docs/faq.mdx
- Dokumentaatio: koti-pc, LM Studio, Ollama, Automatic1111 ja Backup Strategy kansiossa `..\documentation-repo`
