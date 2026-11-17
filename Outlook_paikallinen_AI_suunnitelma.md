# Paikallinen AI-avustaja Classic Outlookiin

Toteutussuunnitelma, 17.11.2026

## Tavoite ja päätökset

Windows-käyttäjäistunnossa toimiva sovellus seuraa käyttäjän valitsemia Classic Outlookin postilaatikoita. Tilit ovat pääosin M365-tilejä, mukana yksi Gmail-tili. Sovellus lukee viestit nykyisen Outlook-profiilin kautta, arvioi ne paikallisella kielimallilla ja kokoaa huomiota vaativat viestit yhteen näkymään. M365-viesteille voidaan lisätä Outlook-luokkia.

Sovellus ei kerää salasanoja eikä kopioi Outlookin kirjautumistunnisteita. Se käyttää Outlookin objektimallia käyttäjän olemassa olevassa istunnossa. Outlook vastaa tilien kirjautumisesta ja synkronoinnista. Erillistä Graph-sovellusrekisteröintiä ei tässä toteutuksessa tarvita. Organisaation paikalliset käyttö- ja tietojenkäsittelyrajoitukset sekä Outlookin ohjelmallisen käytön suojausasetukset voivat silti vaikuttaa toimintaan.

Suositus: C#/.NET, Outlook COM -sovitin, SQLite ja Ollama. LM Studio tuetaan vaihtoehtoisena paikallisena mallipalvelimena. Ensimmäinen käyttöliittymä on yksinkertainen Windowsin WPF-sovellus; erillistä React-palvelinta ei tarvita.

## Paikallisuuden rajaus

- Viestien analyysi tapahtuu omalla koneella ladatulla mallilla.
- Arviot ja käsittelytila tallennetaan käyttäjän paikalliseen AppData-hakemistoon, ei OneDrive-kansioon.
- Tavallinen sähköpostisynkronointi tapahtuu edelleen Microsoftin ja Googlen palveluihin.
- Outlookiin lisätyt luokat synkronoituvat M365-postilaatikkoon. Jos myös AI:n tuottamien merkintöjen pitää pysyä kokonaan paikallisina, valitaan pelkkä paikallinen näkymä.
- Gmailin IMAP-viesteille käytetään paikallisia luokituksia: Outlookin väriluokkia ei oleteta tuetuiksi eikä niitä muuteta Gmail-tunnisteiksi.
- Pilvimallia ei käytetä varavaihtoehtona. Mallipalvelimen puuttuessa työ jää jonoon.

## Käyttökokemus

Ensimmäisellä käynnistyskerralla sovellus näyttää Outlook-profiilissa näkyvät tietovarastot ja kansiot. Käyttäjä valitsee seurattavat laatikot ja kansiot sekä paikallisen mallipalvelimen. Jaetut laatikot otetaan mukaan vain siltä osin kuin ne ovat Outlookissa käytettävissä nykyisillä oikeuksilla.

Alkuun tarjotaan 100–200 viimeaikaisen viestin kokeilu. Luokitukset näkyvät ensin vain sovelluksessa. Käyttäjä voi korjata niitä ja ottaa myöhemmin käyttöön Outlook-luokkien lisäämisen.

Päänäkymässä on kaikkien valittujen laatikoiden yhteinen lista: laatikko, lähettäjä, otsikko, arvioitu toimenpide, kiireellisyys, mahdollinen määräaika ja lyhyt yhteenveto. Viestin voi avata Outlookiin. Saatavilla ovat suodattimet Vastattava, Tehtävä, Odottaa muita ja Tarkista sekä luokituksen korjaus. Näkyvissä ovat myös viimeisen onnistuneen tarkistuksen aika, jonon koko ja laatikkokohtaiset virheet. Sovelluksen voi keskeyttää.

## Arkkitehtuuri

| Osa | Vastuu |
| --- | --- |
| Outlook-sovitin | Laatikoiden ja kansioiden luettelo, viestien luku, avaaminen Outlookissa ja rajatut luokkamuutokset |
| Ajastin ja työjono | Määräaikaistarkistus, käsittelytilat, uudelleenyritykset ja kuormituksen rajoitus |
| Sisällön valmistelu | Pelkkä teksti, pituusrajat, turvallinen katkaisu ja tarvittava keskustelukonteksti |
| Mallisovitin | Ollama tai LM Studio, aikakatkaisu ja JSON Schema -vastauksen tarkistus |
| Säännöt ja toimintalogiikka | Sallitut luokat, käyttäjän korjausten etusija ja Outlookiin kirjoittamisen ehdot |
| SQLite | Viitteet, käsittelytila, arviot, käyttäjän korjaukset ja muutoshistoria |
| WPF-käyttöliittymä | Yhteinen viestilista, tarkastaminen ja asetukset |

Outlook COM -kutsut hoidetaan omalla STA-säikeellä ja tarvittavalla viestipumpulla. COM-olioita ei siirretä mallityöntekijöille: niille annetaan tavallisia tekstimuotoisia tietoja. Mallin odotus ei saa pysäyttää käyttöliittymää tai Outlook-käsittelyä. Viestit luetaan pienissä erissä ja COM-viittaukset vapautetaan hallitusti.

## Viestien löytäminen ja käsittely

1. Alussa luetellaan kaikki valitut tietovarastot; pelkkä profiilin oletus-Inbox ei riitä. Mukaan voidaan valita myös kansiot, joihin Outlook-säännöt siirtävät saapuneita viestejä.
2. Tarkistus tehdään oletuksena kahden minuutin välein. Käynnistyessä ja lepotilasta palattaessa tehdään kiinniottotarkistus.
3. Kansiokohtainen tarkistuspiste ja päällekkäinen aikaikkuna löytävät viiveellä näkyvät viestit. Lisäksi tehdään rajattu viime päivien täsmäytys. Näitä verrataan tietokannan käsittelytilaan; koko laatikkoa ei lueta joka kierroksella.
4. Viestistä luetaan lähettäjä, vastaanottajat, otsikko, vastaanottoaika ja tekstisisältö. Viestiä ei merkitä luetuksi. Muiden Outlook-kohteiden kuin sähköpostien käsittely ohitetaan alussa.
5. Sääntöosuma voi ohittaa mallin vain selkeissä käyttäjän määrittelemissä tapauksissa, kuten tietyn uutiskirjeen kohdalla.
6. Malli tuottaa rakenteisen arvion, joka tarkistetaan ohjelmallisesti. Virheellinen vastaus voidaan yrittää kerran uudelleen; jatkuva virhe näkyy tarkistettavana eikä onnistuneena luokituksena.
7. Tulos tallennetaan ja mahdolliset sallitut Outlook-luokat lisätään erillisessä kirjoitusvaiheessa.

Tunnistuksessa säilytetään StoreID ja EntryID avaamista varten, mutta niitä ei käsitellä ikuisesti pysyvinä tunnisteina. Siirrot ja kopiot huomioidaan laatikkokohtaisilla lisätiedoilla, kuten Internet Message-ID:llä ja sisältötiivisteellä. Message-ID voi puuttua tai esiintyä kopioissa: yksinään se ei riitä. Epäselvässä tapauksessa vältetään kirjoittamista väärään viestiin. Pelkkä AI-luokan lisäämisen aiheuttama muutos ei saa käynnistää uutta malliajoa.

Ensimmäinen versio arvioi uuden viestin, ei lupaa täydellistä keskustelun tehtävätilaa. Odottaa muita -tilan ja vastattu/hoidettu-päätelmien luotettava ylläpito edellyttää myöhemmin myös lähetettyjen viestien ja ketjujen seurantaa.

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

## Malli ja tietojen käsittely

Jos aiemmin mainittu RTX 2070 SUPER 8 Gt on yhä käytössä, aloitetaan 7–8B-kokoisen nelibittisen monikielisen mallin kokeilulla. Malli valitaan suomen- ja englanninkielisen oman arviointiaineiston perusteella. Konteksti pidetään maltillisena, yksi malliajo kerrallaan; nopeus ja muistinkulutus mitataan oikealla koneella.

Ollaman pilviominaisuudet poistetaan käytöstä. Mallirajapinta sallii vain koneen paikalliset loopback-osoitteet. Ei web-hakua, ulkoisia työkaluja eikä automaattista mallilatausta viestin käsittelyn aikana. LM Studio -tuki toteutetaan vaihtoehtoisena sovittimena.

Viestisisältö on epäluotettua aineistoa: sen sisältämät ohjeet eivät saa muuttaa järjestelmän toimintaa. Malli antaa vain arvion; se ei saa COM-pääsyä, tiedostojärjestelmätyökaluja tai sähköpostin lähetysoikeutta sovelluksen kautta. Sallitut toiminnot ja kohteet valitsee ohjelmakoodi.

Raakaviestiteksti käsitellään oletuksena muistissa eikä tallenneta pysyvään tietokantaan. Tietokantaan jäävät myös arkaluonteisia tietoja sisältävät yhteenvedot, joten se sijoitetaan käyttäjäkohtaiseen hakemistoon Windows-käyttöoikeuksilla. Levyn salaus suojaa levossa olevia tietoja. Lokeihin ei kirjoiteta sisältöä tai henkilötietoja oletuksena. Säilytysaika on säädettävä, oletus 90 päivää; käyttäjä voi poistaa paikalliset arviot. Liitteitä ja ulkoisia kuvia ei avata ensimmäisessä versiossa.

## Toteutusvaiheet ja hyväksymisehdot

### 1. Outlook-yhteyden kokeilu — ensimmäinen toteutettava työ

Pieni C#-konsoliohjelma luettelee laatikot ja valitut kansiot, lukee kustakin esimerkiksi kymmenen uusinta sähköpostia sekä avaa viitteen perusteella viestin Outlookissa. Sisältöä ei lähetetä mallille eikä tallenneta lokiin. Yhdelle erikseen valitulle M365-testiviestille kokeillaan AI-luokan lisäystä ja poistoa.

Hyväksytty, kun kaikki tarkoitetut M365-laatikot ja Gmail ovat luettavissa, muiden luokkien ja lukutilan säilyminen on todettu sekä suojauskehotteiden tai organisaatiorajoitusten vaikutus tunnetaan. Jos suojausasetus estää käytön, sitä ei poisteta automaattisesti.

### 2. Paikallinen arviointi

Toteutetaan SQLite-jono, tekstin valmistelu, Ollama-sovitin ja vastauksen tarkistus. Ajetaan 100–200 viestin otos ilman Outlook-muutoksia. Mallin versio, kehote ja asetukset tallennetaan arvioihin.

Hyväksytty, kun vastaukset noudattavat skeemaa, katkaistu sisältö näkyy, mallin puuttuminen jättää työt jonoon ja kaikki mallikutsut pysyvät paikallisina.

### 3. Yhteinen näkymä ja laadun arviointi

Rakennetaan WPF-lista, suodatus, avaaminen Outlookissa ja käyttäjän korjaukset. Käyttäjä merkitsee otoksen viestit itse. Kehotetta säädetään vain osalla aineistosta ja tulos arvioidaan erillisellä osalla.

Mitataan erityisesti huomiota vaativien viestien löytyminen ja virheelliset hälytykset. Alustava tavoite on vähintään 95 %:n löytymisosuus arviointiotoksen toimenpiteitä vaativille viesteille, mutta tulos raportoidaan myös lukumäärinä; pieni otos ei takaa vastaavaa laatua jatkossa. Jos aineistossa on liian vähän tällaisia viestejä, otosta kasvatetaan. Ennen luokkien automaatiota käydään läpi jokainen huomiotta jäänyt viesti ja asetetaan hyväksyttävä hälytystaso käytännön kokemuksen mukaan.

### 4. Jatkuva seuranta ja luokat

Lisätään kahden minuutin tarkistus, keskeytys/jatkaminen, kiinniotto, rajatut uudelleenyritykset ja valinnainen M365-luokkien kirjoitus. Gmail näkyy samassa listassa paikallisin luokin. Luokkamuutoksista säilytetään palautukseen tarvittava historia; palautus poistaa vain sovelluksen omat muutokset huomioiden myöhemmät käyttäjämuutokset.

Hyväksytty, kun lepotila, Outlookin sulkeminen ja uudelleenkäynnistys, verkkokatko, mallin uudelleenkäynnistys, kansiosiirto ja vain luku -kansio eivät hukkaa käsittelytilaa tai aiheuta toistuvia muutoksia. Viestien avaus tai arviointi ei merkitse niitä luetuiksi. Käyttöönoton jälkeen seurataan käytännön toimivuutta noin viikon ajan.

### 5. Käyttöönotto

Paketoidaan käyttäjäkohtainen Windows-sovellus ja ohjeet. Käynnistys kirjautuessa on valinnainen ja toimii vain kirjautuneen käyttäjän istunnossa. Ohjelma odottaa Outlookia, jos se ei ole käynnissä; suljettua Outlookia ei avata huomaamatta. Mallipalvelimen käynnistyksestä annetaan erillinen selkeä asetus. Ohjelmaa ei asenneta Windows-palveluksi. Asennustapa ja Office-yhteensopivuus varmistetaan oikealla koneella.

## Ensiversion ulkopuolelle jäävät asiat

Liitteiden analyysi, OCR, automaattinen lähetys, poistaminen ja kansiosiirrot sekä täydellinen tehtävien valmistumisen päättely. Myöhempiä laajennuksia ovat lähetettyjen viestien seuranta, keskustelukonteksti, paikallinen haku ja aamukooste. Niiden hyödyllisyys arvioidaan perusluokittelun jälkeen.

## Riippuvuudet ja rajoitukset

Tämä on suunnitelma, ei vielä käyttäjän Outlook-ympäristössä testattu toteutus. Tarvitaan Windows, Classic Outlook, toimiva profiili ja paikallinen mallipalvelin. Työ tehdään käyttäjän oikeuksilla; organisaatiokäytännöt voivat silti estää sovelluksen ajamisen tai Outlook-automaation. Gmailista näkyy Outlookiin synkronoitu sisältö ja Gmailin kansiorakenteen aiheuttamat kopiot pitää huomioida. Käyttäjän pitää valita sääntöjen siirtokansiot seurantaan, jos niiden viestejä halutaan arvioida.

## Lähteet

- Microsoft: Useita tilejä samassa Outlook-profiilissa: https://learn.microsoft.com/en-us/office/vba/outlook/Concepts/Accounts/using-multiple-accounts-for-the-same-profile-on-outlook
- Microsoft: Store.GetDefaultFolder: https://learn.microsoft.com/en-us/office/vba/api/Outlook.Store.GetDefaultFolder
- Microsoft: EntryID ja StoreID: https://learn.microsoft.com/en-us/office/vba/outlook/how-to/items-folders-and-stores/working-with-entryids-and-storeids
- Microsoft: MailItem.Categories: https://learn.microsoft.com/en-us/office/vba/api/outlook.mailitem.categories
- Microsoft: Outlook-objektimallin suojaus: https://learn.microsoft.com/en-us/office/vba/outlook/how-to/security/security-behavior-of-the-outlook-object-model
- Microsoft: Objektimalli ei sovellu Windows-palveluun: https://learn.microsoft.com/en-us/microsoft-365-apps/outlook/user-interface/oom-unsuitable-in-windows-service
- Microsoft: Outlook-luokat ja IMAP-rajoitus: https://support.microsoft.com/en-us/outlook/mail/create-and-assign-color-categories-in-outlook
- Ollama: paikallisuus ja pilviominaisuuksien poistaminen: https://github.com/ollama/ollama/blob/main/docs/faq.mdx
- LM Studio: offline-käyttö: https://lmstudio.ai/docs/app/offline
