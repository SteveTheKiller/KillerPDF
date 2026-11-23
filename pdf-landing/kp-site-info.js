(function () {
  'use strict';
  var copy = {
  "sl": [
    "Prenosne kopije shranjujejo nastavitve, shranjene podpise in modele OCR v <code>KillerPDF-Data</code> ob zaganjalniku. Izvorne knjižnice OCR in začasne delovne datoteke še vedno uporabljajo <code>%LOCALAPPDATA%\\KillerPDF</code>.",
    "Vsi jezikovni modeli OCR, tudi angleški, se prenesejo ob prvi uporabi in ostanejo v predpomnilniku. Izvorne knjižnice Tesseract se razširijo v predpomnilnik OCR za posamezno različico.",
    "Počisti vse podatke po možnosti odstrani shranjene nastavitve, modele OCR in začasne datoteke. Zaklenjene datoteke lahko ostanejo. Nameščene kopije ohranijo shranjene podpise in dnevnike, prenosne kopije pa odstranijo tudi mapo <code>KillerPDF-Data</code>. Izvorni PDF-ji se ne izbrišejo.",
    "Strukturne spremembe zapišejo in ponovno odprejo začasni PDF prek The KillerPDF.Engine. Po potrebi se obnovijo zasuki strani in čakajoče opombe. Izvirna datoteka se spremeni šele ob shranjevanju.",
    "Položaji opomb uporabljajo enote render-dim, merjene od zgornjega levega kota, ki se pri povečavi ne spreminjajo. Koordinate PDF se pretvorijo ob branju in pisanju vsebine. Isti položaji se uporabljajo v vseh načinih pogleda.",
    "Preteklo preverjanje standardov",
    "Natančno število izvornih datotek in preizkusov je v Pregledu. Rezultati zbirke določajo preizkušeno gradnjo in vhodne podatke. qpdf, veraPDF in OpenSSL ločeno preverjajo strukturo, standarde in podpise.",
    "Potrdi besedilo",
    "Odpiranje in popravljanje PDF-jev"
  ],
  "en": [
    "Portable copies store settings, saved signatures and OCR models in <code>KillerPDF-Data</code> beside the launcher. Native OCR libraries and temporary working files still use <code>%LOCALAPPDATA%\\KillerPDF</code>.",
    "All OCR language models, including English, download on first use and remain cached. Native Tesseract libraries extract to the versioned OCR cache.",
    "Clear all Data removes saved settings, OCR models and temporary files where possible. Locked files may remain. Installed copies keep saved signatures and logs; portable copies also remove the <code>KillerPDF-Data</code> folder. Your source PDFs are not deleted.",
    "Structural edits write and reopen a temporary PDF through The KillerPDF.Engine. Page rotations and pending annotations are restored as needed. Your original file changes only when you save.",
    "Annotation positions use zoom-stable render-dim units measured from the top-left. PDF coordinates are converted when content is read or written; the same positions feed every view mode.",
    "Historical standards validation",
    "For exact source and test counts, see the Overview. Corpus results identify the tested build and inputs. qpdf, veraPDF and OpenSSL provide separate structural, standards and signature checks.",
    "Commit text",
    "Open & repair PDFs"
  ],
  "es": [
    "La edición portátil guarda la configuración, las firmas y los modelos OCR en <code>KillerPDF-Data</code>, junto al lanzador. Las bibliotecas nativas de OCR y los archivos temporales siguen usando <code>%LOCALAPPDATA%\\KillerPDF</code>.",
    "Todos los modelos OCR, incluido el inglés, se descargan al usarlos por primera vez y quedan en caché. Las bibliotecas nativas de Tesseract se extraen en la caché OCR de cada versión.",
    "Borrar todos los datos elimina la configuración, los modelos OCR y los archivos temporales que pueda borrar. Los archivos bloqueados pueden permanecer. La edición instalada conserva las firmas y los registros; la portátil también elimina <code>KillerPDF-Data</code>. No se borran los PDF originales.",
    "Los cambios estructurales escriben y vuelven a abrir un PDF temporal mediante The KillerPDF.Engine. Las rotaciones y las anotaciones pendientes se restauran cuando corresponde. El original solo cambia al guardar.",
    "Las posiciones de las anotaciones usan unidades render-dim estables al cambiar el zoom, con origen arriba a la izquierda. Las coordenadas PDF se convierten al leer o escribir contenido y se reutilizan en todas las vistas.",
    "Validación histórica de estándares",
    "Los recuentos de código y pruebas están en Resumen. Los resultados del corpus identifican la versión probada y las entradas. qpdf, veraPDF y OpenSSL comprueban por separado la estructura, los estándares y las firmas.",
    "Confirmar texto",
    "Abrir y reparar PDF"
  ],
  "de": [
    "Die portable Ausgabe speichert Einstellungen, Signaturen und OCR-Modelle in <code>KillerPDF-Data</code> neben dem Starter. Native OCR-Bibliotheken und temporäre Arbeitsdateien liegen weiterhin unter <code>%LOCALAPPDATA%\\KillerPDF</code>.",
    "Alle OCR-Sprachmodelle, auch Englisch, werden beim ersten Gebrauch heruntergeladen und zwischengespeichert. Native Tesseract-Bibliotheken werden in den versionsbezogenen OCR-Cache entpackt.",
    "Alle Daten löschen entfernt Einstellungen, OCR-Modelle und temporäre Dateien soweit möglich. Gesperrte Dateien können erhalten bleiben. Installierte Ausgaben behalten Signaturen und Protokolle; portable Ausgaben entfernen auch <code>KillerPDF-Data</code>. Die ursprünglichen PDFs werden nicht gelöscht.",
    "Strukturelle Änderungen schreiben eine temporäre PDF mit The KillerPDF.Engine und öffnen sie erneut. Seitendrehungen und ausstehende Anmerkungen werden bei Bedarf wiederhergestellt. Das Original ändert sich erst beim Speichern.",
    "Anmerkungspositionen verwenden zoomunabhängige render-dim-Einheiten mit Ursprung links oben. PDF-Koordinaten werden beim Lesen oder Schreiben umgerechnet; alle Ansichten verwenden dieselben Positionen.",
    "Historische Standardvalidierung",
    "Quellcode- und Testzahlen stehen in der Übersicht. Corpus-Ergebnisse nennen den getesteten Build und die Eingaben. qpdf, veraPDF und OpenSSL prüfen Struktur, Standards und Signaturen getrennt.",
    "Text übernehmen",
    "PDFs öffnen und reparieren"
  ],
  "fr": [
    "L'édition portable conserve les paramètres, les signatures et les modèles OCR dans <code>KillerPDF-Data</code>, à côté du lanceur. Les bibliothèques OCR natives et les fichiers temporaires restent dans <code>%LOCALAPPDATA%\\KillerPDF</code>.",
    "Tous les modèles OCR, y compris l'anglais, sont téléchargés à la première utilisation puis conservés en cache. Les bibliothèques natives de Tesseract sont extraites dans le cache OCR de chaque version.",
    "Effacer toutes les données supprime les paramètres, les modèles OCR et les fichiers temporaires lorsque c'est possible. Des fichiers verrouillés peuvent rester. L'édition installée conserve les signatures et les journaux ; l'édition portable supprime aussi <code>KillerPDF-Data</code>. Les PDF originaux ne sont pas supprimés.",
    "Les modifications structurelles écrivent puis rouvrent un PDF temporaire avec The KillerPDF.Engine. Les rotations et annotations en attente sont restaurées si nécessaire. L'original ne change que lors de l'enregistrement.",
    "Les positions des annotations utilisent des unités render-dim stables au zoom, depuis le coin supérieur gauche. Les coordonnées PDF sont converties à la lecture ou à l'écriture et partagées entre toutes les vues.",
    "Validation historique des normes",
    "Les nombres de fichiers source et de tests figurent dans Vue d'ensemble. Les résultats du corpus indiquent le build testé et les entrées. qpdf, veraPDF et OpenSSL vérifient séparément la structure, les normes et les signatures.",
    "Valider le texte",
    "Ouvrir et réparer les PDF"
  ],
  "it": [
    "L'edizione portatile conserva impostazioni, firme e modelli OCR in <code>KillerPDF-Data</code>, accanto al programma di avvio. Le librerie OCR native e i file temporanei restano in <code>%LOCALAPPDATA%\\KillerPDF</code>.",
    "Tutti i modelli OCR, incluso l'inglese, vengono scaricati al primo utilizzo e conservati nella cache. Le librerie native di Tesseract vengono estratte nella cache OCR della versione.",
    "Cancella tutti i dati rimuove impostazioni, modelli OCR e file temporanei quando possibile. I file bloccati possono rimanere. L'edizione installata conserva firme e registri; quella portatile rimuove anche <code>KillerPDF-Data</code>. I PDF originali non vengono eliminati.",
    "Le modifiche strutturali scrivono e riaprono un PDF temporaneo con The KillerPDF.Engine. Rotazioni e annotazioni in sospeso vengono ripristinate quando necessario. L'originale cambia solo quando si salva.",
    "Le posizioni delle annotazioni usano unità render-dim stabili rispetto allo zoom, con origine in alto a sinistra. Le coordinate PDF vengono convertite durante lettura o scrittura e condivise da tutte le viste.",
    "Validazione storica degli standard",
    "I conteggi di sorgenti e test sono nella Panoramica. I risultati del corpus indicano la build verificata e i file usati. qpdf, veraPDF e OpenSSL controllano separatamente struttura, standard e firme.",
    "Conferma testo",
    "Apri e ripara PDF"
  ],
  "pt": [
    "A edição portátil guarda configurações, assinaturas e modelos OCR em <code>KillerPDF-Data</code>, ao lado do inicializador. As bibliotecas nativas de OCR e os arquivos temporários continuam em <code>%LOCALAPPDATA%\\KillerPDF</code>.",
    "Todos os modelos OCR, inclusive o inglês, são baixados no primeiro uso e ficam em cache. As bibliotecas nativas do Tesseract são extraídas para o cache OCR de cada versão.",
    "Limpar todos os dados remove configurações, modelos OCR e arquivos temporários quando possível. Arquivos bloqueados podem permanecer. A edição instalada mantém assinaturas e registros; a portátil também remove <code>KillerPDF-Data</code>. Os PDFs originais não são excluídos.",
    "As alterações estruturais gravam e reabrem um PDF temporário pelo The KillerPDF.Engine. Rotações e anotações pendentes são restauradas quando necessário. O original só muda ao salvar.",
    "As posições das anotações usam unidades render-dim estáveis com o zoom, a partir do canto superior esquerdo. As coordenadas PDF são convertidas ao ler ou gravar conteúdo e compartilhadas entre todas as visualizações.",
    "Validação histórica de padrões",
    "As contagens de código e testes estão em Visão geral. Os resultados do corpus identificam a compilação testada e os arquivos usados. qpdf, veraPDF e OpenSSL verificam separadamente estrutura, padrões e assinaturas.",
    "Confirmar texto",
    "Abrir e reparar PDFs"
  ],
  "nb": [
    "Den portable utgaven lagrer innstillinger, signaturer og OCR-modeller i <code>KillerPDF-Data</code> ved siden av startprogrammet. Native OCR-biblioteker og midlertidige arbeidsfiler bruker fortsatt <code>%LOCALAPPDATA%\\KillerPDF</code>.",
    "Alle OCR-språkmodeller, også engelsk, lastes ned ved første bruk og blir lagret i hurtigbufferen. Native Tesseract-biblioteker pakkes ut i versjonens OCR-hurtigbuffer.",
    "Slett alle data fjerner innstillinger, OCR-modeller og midlertidige filer når det er mulig. Låste filer kan bli igjen. Den installerte utgaven beholder signaturer og logger; den portable fjerner også <code>KillerPDF-Data</code>. De opprinnelige PDF-filene slettes ikke.",
    "Strukturelle endringer skriver en midlertidig PDF med The KillerPDF.Engine og åpner den på nytt. Siderotasjoner og ventende merknader gjenopprettes ved behov. Originalen endres først når du lagrer.",
    "Merknadsposisjoner bruker render-dim-enheter som er stabile ved zoom, med utgangspunkt øverst til venstre. PDF-koordinater omregnes ved lesing eller skriving og deles mellom alle visningsmoduser.",
    "Historisk standardvalidering",
    "Antall kildefiler og tester står i Oversikt. Corpus-resultatene viser testet bygg og inndata. qpdf, veraPDF og OpenSSL kontrollerer struktur, standarder og signaturer hver for seg.",
    "Bekreft tekst",
    "Åpne og reparere PDF-er"
  ],
  "pl": [
    "Wersja przenośna przechowuje ustawienia, podpisy i modele OCR w <code>KillerPDF-Data</code> obok programu uruchamiającego. Natywne biblioteki OCR i pliki tymczasowe nadal znajdują się w <code>%LOCALAPPDATA%\\KillerPDF</code>.",
    "Wszystkie modele językowe OCR, także angielski, są pobierane przy pierwszym użyciu i przechowywane w pamięci podręcznej. Natywne biblioteki Tesseract są wypakowywane do pamięci podręcznej OCR danej wersji.",
    "Wyczyść wszystkie dane usuwa ustawienia, modele OCR i pliki tymczasowe, gdy jest to możliwe. Zablokowane pliki mogą pozostać. Wersja zainstalowana zachowuje podpisy i dzienniki; przenośna usuwa także <code>KillerPDF-Data</code>. Oryginalne pliki PDF nie są usuwane.",
    "Zmiany strukturalne zapisują i ponownie otwierają tymczasowy PDF przez The KillerPDF.Engine. Obrót stron i oczekujące adnotacje są przywracane w razie potrzeby. Oryginał zmienia się dopiero po zapisaniu.",
    "Pozycje adnotacji używają jednostek render-dim niezależnych od powiększenia, liczonych od lewego górnego rogu. Współrzędne PDF są przeliczane podczas odczytu lub zapisu i wspólne dla wszystkich widoków.",
    "Historyczna walidacja standardów",
    "Liczby plików źródłowych i testów są w Przeglądzie. Wyniki corpus wskazują testowaną kompilację i dane wejściowe. qpdf, veraPDF i OpenSSL osobno sprawdzają strukturę, standardy i podpisy.",
    "Zatwierdź tekst",
    "Otwieranie i naprawa PDF"
  ],
  "cs": [
    "Přenosná verze ukládá nastavení, podpisy a modely OCR do <code>KillerPDF-Data</code> vedle spouštěče. Nativní knihovny OCR a dočasné pracovní soubory zůstávají v <code>%LOCALAPPDATA%\\KillerPDF</code>.",
    "Všechny jazykové modely OCR včetně angličtiny se stáhnou při prvním použití a zůstanou v mezipaměti. Nativní knihovny Tesseract se rozbalí do mezipaměti OCR dané verze.",
    "Vymazat všechna data odstraní nastavení, modely OCR a dočasné soubory, pokud je to možné. Zamčené soubory mohou zůstat. Nainstalovaná verze ponechá podpisy a protokoly; přenosná odstraní i <code>KillerPDF-Data</code>. Původní PDF se nesmažou.",
    "Strukturální změny zapisují a znovu otevírají dočasné PDF pomocí The KillerPDF.Engine. Otočení stránek a čekající anotace se podle potřeby obnoví. Originál se změní až při uložení.",
    "Pozice anotací používají jednotky render-dim nezávislé na přiblížení, měřené z levého horního rohu. Souřadnice PDF se převádějí při čtení nebo zápisu a sdílejí je všechny režimy zobrazení.",
    "Historické ověření standardů",
    "Počty zdrojových souborů a testů jsou v Přehledu. Výsledky corpus uvádějí testované sestavení a vstupy. qpdf, veraPDF a OpenSSL samostatně ověřují strukturu, standardy a podpisy.",
    "Potvrdit text",
    "Otevírání a oprava PDF"
  ],
  "hu": [
    "A hordozható kiadás a beállításokat, aláírásokat és OCR-modelleket az indító melletti <code>KillerPDF-Data</code> mappában tárolja. A natív OCR-könyvtárak és ideiglenes munkafájlok továbbra is a <code>%LOCALAPPDATA%\\KillerPDF</code> alatt vannak.",
    "Minden OCR-nyelvi modell, az angol is, az első használatkor töltődik le, és gyorsítótárban marad. A natív Tesseract-könyvtárak a verzióhoz tartozó OCR-gyorsítótárba kerülnek.",
    "Az összes adat törlése lehetőség szerint eltávolítja a beállításokat, OCR-modelleket és ideiglenes fájlokat. A zárolt fájlok megmaradhatnak. A telepített kiadás megtartja az aláírásokat és naplókat; a hordozható a <code>KillerPDF-Data</code> mappát is törli. Az eredeti PDF-eket nem törli.",
    "A szerkezeti módosítások ideiglenes PDF-et írnak és nyitnak meg újra a The KillerPDF.Engine segítségével. Az oldalforgatások és függőben lévő megjegyzések szükség szerint visszaállnak. Az eredeti csak mentéskor változik.",
    "A megjegyzések helye a bal felső saroktól mért, nagyítástól független render-dim-egységeket használ. A PDF-koordináták olvasáskor vagy íráskor átalakulnak, és minden nézet ugyanazokat használja.",
    "Korábbi szabványellenőrzés",
    "A forrásfájlok és tesztek száma az Áttekintésben található. A corpus eredményei megadják a tesztelt buildet és bemeneteket. A qpdf, veraPDF és OpenSSL külön ellenőrzi a szerkezetet, szabványokat és aláírásokat.",
    "Szöveg véglegesítése",
    "PDF-ek megnyitása és javítása"
  ],
  "tr": [
    "Taşınabilir sürüm ayarları, imzaları ve OCR modellerini başlatıcının yanındaki <code>KillerPDF-Data</code> klasöründe saklar. Yerel OCR kitaplıkları ve geçici çalışma dosyaları hâlâ <code>%LOCALAPPDATA%\\KillerPDF</code> kullanır.",
    "İngilizce dahil tüm OCR dil modelleri ilk kullanımda indirilir ve önbellekte tutulur. Yerel Tesseract kitaplıkları sürüme ait OCR önbelleğine açılır.",
    "Tüm Verileri Temizle, mümkün olduğunda ayarları, OCR modellerini ve geçici dosyaları kaldırır. Kilitli dosyalar kalabilir. Kurulu sürüm imzaları ve günlükleri korur; taşınabilir sürüm <code>KillerPDF-Data</code> klasörünü de kaldırır. Kaynak PDF'leriniz silinmez.",
    "Yapısal değişiklikler The KillerPDF.Engine ile geçici bir PDF yazıp yeniden açar. Sayfa dönüşleri ve bekleyen ek açıklamalar gerektiğinde geri yüklenir. Asıl dosya yalnızca kaydettiğinizde değişir.",
    "Ek açıklama konumları sol üst köşeden ölçülen, yakınlaştırmadan bağımsız render-dim birimlerini kullanır. PDF koordinatları içerik okunurken veya yazılırken dönüştürülür ve tüm görünüm modlarında paylaşılır.",
    "Geçmiş standart doğrulaması",
    "Kaynak ve test sayıları Genel Bakış'tadır. Corpus sonuçları test edilen derlemeyi ve girdileri belirtir. qpdf, veraPDF ve OpenSSL yapı, standart ve imzaları ayrı ayrı denetler.",
    "Metni onayla",
    "PDF açma ve onarma"
  ],
  "ru": [
    "Переносная версия хранит настройки, подписи и модели OCR в <code>KillerPDF-Data</code> рядом с программой запуска. Нативные библиотеки OCR и временные рабочие файлы по-прежнему используют <code>%LOCALAPPDATA%\\KillerPDF</code>.",
    "Все языковые модели OCR, включая английскую, загружаются при первом использовании и сохраняются в кэше. Нативные библиотеки Tesseract распаковываются в кэш OCR соответствующей версии.",
    "Очистить все данные удаляет настройки, модели OCR и временные файлы, когда это возможно. Заблокированные файлы могут остаться. Установленная версия сохраняет подписи и журналы; переносная также удаляет папку <code>KillerPDF-Data</code>. Исходные PDF не удаляются.",
    "Структурные изменения записывают и снова открывают временный PDF через The KillerPDF.Engine. Повороты страниц и несохранённые аннотации восстанавливаются при необходимости. Оригинал меняется только при сохранении.",
    "Позиции аннотаций используют единицы render-dim, не зависящие от масштаба, с отсчётом от верхнего левого угла. Координаты PDF преобразуются при чтении или записи и используются во всех режимах просмотра.",
    "Историческая проверка стандартов",
    "Число исходных файлов и тестов указано в Обзоре. Результаты corpus называют проверенную сборку и входные файлы. qpdf, veraPDF и OpenSSL отдельно проверяют структуру, стандарты и подписи.",
    "Подтвердить текст",
    "Открытие и восстановление PDF"
  ],
  "uk": [
    "Портативна версія зберігає налаштування, підписи та моделі OCR у <code>KillerPDF-Data</code> поруч із програмою запуску. Нативні бібліотеки OCR і тимчасові робочі файли й надалі використовують <code>%LOCALAPPDATA%\\KillerPDF</code>.",
    "Усі мовні моделі OCR, включно з англійською, завантажуються під час першого використання та зберігаються в кеші. Нативні бібліотеки Tesseract розпаковуються до кешу OCR відповідної версії.",
    "Очистити всі дані видаляє налаштування, моделі OCR та тимчасові файли, коли це можливо. Заблоковані файли можуть залишитися. Встановлена версія зберігає підписи й журнали; портативна також видаляє папку <code>KillerPDF-Data</code>. Початкові PDF не видаляються.",
    "Структурні зміни записують і знову відкривають тимчасовий PDF через The KillerPDF.Engine. Повороти сторінок і незбережені анотації відновлюються за потреби. Оригінал змінюється лише під час збереження.",
    "Позиції анотацій використовують одиниці render-dim, незалежні від масштабу, з відліком від верхнього лівого кута. Координати PDF перетворюються під час читання або запису та використовуються в усіх режимах перегляду.",
    "Історична перевірка стандартів",
    "Кількість вихідних файлів і тестів указано в Огляді. Результати corpus називають перевірену збірку й вхідні файли. qpdf, veraPDF та OpenSSL окремо перевіряють структуру, стандарти й підписи.",
    "Підтвердити текст",
    "Відкриття та відновлення PDF"
  ],
  "kk": [
    "Тасымалды нұсқа параметрлерді, қолтаңбаларды және OCR модельдерін іске қосқыштың жанындағы <code>KillerPDF-Data</code> қалтасында сақтайды. Жергілікті OCR кітапханалары мен уақытша жұмыс файлдары әлі де <code>%LOCALAPPDATA%\\KillerPDF</code> ішінде болады.",
    "Барлық OCR тіл модельдері, соның ішінде ағылшын тілі, алғаш қолданғанда жүктеліп, кэште сақталады. Жергілікті Tesseract кітапханалары нұсқаға арналған OCR кэшіне шығарылады.",
    "Барлық деректерді тазалау мүмкін болғанда параметрлерді, OCR модельдерін және уақытша файлдарды жояды. Құлыпталған файлдар қалуы мүмкін. Орнатылған нұсқа қолтаңбалар мен журналдарды сақтайды; тасымалды нұсқа <code>KillerPDF-Data</code> қалтасын да жояды. Бастапқы PDF файлдары жойылмайды.",
    "Құрылымдық өзгерістер The KillerPDF.Engine арқылы уақытша PDF жазып, қайта ашады. Бет бұрылыстары мен сақталмаған аннотациялар қажет болғанда қалпына келеді. Түпнұсқа тек сақтағанда өзгереді.",
    "Аннотация орны жоғарғы сол жақтан өлшенетін, масштабтан тәуелсіз render-dim бірліктерін қолданады. PDF координаттары оқу не жазу кезінде түрлендіріліп, барлық көріністе пайдаланылады.",
    "Бұрынғы стандарт тексеруі",
    "Бастапқы файлдар мен тест саны Шолу бөлімінде көрсетілген. Corpus нәтижелері тексерілген құрастырылым мен кіріс файлдарын атайды. qpdf, veraPDF және OpenSSL құрылымды, стандарттарды және қолтаңбаларды бөлек тексереді.",
    "Мәтінді бекіту",
    "PDF ашу және жөндеу"
  ],
  "vi": [
    "Bản portable lưu cài đặt, chữ ký và mô hình OCR trong <code>KillerPDF-Data</code> bên cạnh tệp khởi chạy. Thư viện OCR gốc và tệp làm việc tạm vẫn dùng <code>%LOCALAPPDATA%\\KillerPDF</code>.",
    "Mọi mô hình ngôn ngữ OCR, kể cả tiếng Anh, đều được tải ở lần dùng đầu tiên và lưu trong bộ nhớ đệm. Thư viện Tesseract gốc được giải nén vào bộ nhớ đệm OCR của từng phiên bản.",
    "Xóa tất cả dữ liệu sẽ xóa cài đặt, mô hình OCR và tệp tạm khi có thể. Tệp bị khóa có thể vẫn còn. Bản cài đặt giữ lại chữ ký và nhật ký; bản portable còn xóa thư mục <code>KillerPDF-Data</code>. Tệp PDF gốc không bị xóa.",
    "Thay đổi cấu trúc ghi rồi mở lại một PDF tạm qua The KillerPDF.Engine. Góc xoay trang và chú thích chưa lưu được khôi phục khi cần. Tệp gốc chỉ thay đổi khi bạn lưu.",
    "Vị trí chú thích dùng đơn vị render-dim không đổi theo độ thu phóng, tính từ góc trên bên trái. Tọa độ PDF được chuyển đổi khi đọc hoặc ghi và dùng chung cho mọi chế độ xem.",
    "Kiểm tra tiêu chuẩn trước đây",
    "Số tệp mã nguồn và bài kiểm tra có trong Tổng quan. Kết quả corpus nêu bản dựng và dữ liệu đã kiểm tra. qpdf, veraPDF và OpenSSL kiểm tra riêng cấu trúc, tiêu chuẩn và chữ ký.",
    "Xác nhận văn bản",
    "Mở và sửa PDF"
  ],
  "ja": [
    "ポータブル版は設定、署名、OCRモデルを起動ファイルの隣の <code>KillerPDF-Data</code> に保存します。OCRのネイティブライブラリと作業用一時ファイルは引き続き <code>%LOCALAPPDATA%\\KillerPDF</code> を使います。",
    "英語を含むすべてのOCR言語モデルは初回使用時にダウンロードされ、キャッシュに保存されます。Tesseractのネイティブライブラリは各バージョンのOCRキャッシュに展開されます。",
    "すべてのデータを消去は、可能な範囲で設定、OCRモデル、一時ファイルを削除します。使用中のファイルは残る場合があります。インストール版は署名とログを残し、ポータブル版は <code>KillerPDF-Data</code> も削除します。元のPDFは削除しません。",
    "構造の編集ではThe KillerPDF.Engineで一時PDFを書き出し、開き直します。必要に応じてページの回転や未保存の注釈を復元します。元のファイルが変わるのは保存したときだけです。",
    "注釈の位置は左上を基準とする、ズームに依存しないrender-dim単位で保持します。PDF座標は読み書きの際に変換され、すべての表示モードで同じ位置を使います。",
    "過去の規格検証",
    "ソースとテストの件数は概要にあります。Corpusの結果には検証したビルドと入力を記載しています。qpdf、veraPDF、OpenSSLは構造、規格、署名を個別に検証します。",
    "テキストを確定",
    "PDFを開いて修復"
  ],
  "zh": [
    "可攜版將設定、簽名和 OCR 模型儲存在啟動檔旁的 <code>KillerPDF-Data</code>。OCR 原生程式庫和暫存工作檔仍使用 <code>%LOCALAPPDATA%\\KillerPDF</code>。",
    "所有 OCR 語言模型，包括英文，都在首次使用時下載並保留在快取中。Tesseract 原生程式庫會解壓縮至各版本的 OCR 快取。",
    "清除所有資料會盡可能移除設定、OCR 模型和暫存檔。鎖定中的檔案可能保留。安裝版保留簽名和記錄；可攜版也會移除 <code>KillerPDF-Data</code> 資料夾。原始 PDF 不會被刪除。",
    "結構編輯透過 The KillerPDF.Engine 寫入暫存 PDF 並重新開啟，必要時恢復頁面旋轉與尚未儲存的註解。原始檔案僅在儲存時變更。",
    "註解位置使用從左上角量起、不隨縮放改變的 render-dim 單位。PDF 座標在讀寫內容時轉換，所有檢視模式共用相同位置。",
    "歷史標準驗證",
    "原始碼與測試數量請見概覽。Corpus 結果會標明測試的建置版本與輸入。qpdf、veraPDF 和 OpenSSL 分別檢查結構、標準和簽章。",
    "確認文字",
    "開啟及修復 PDF"
  ],
  "zh-cn": [
    "便携版将设置、签名和 OCR 模型保存在启动文件旁的 <code>KillerPDF-Data</code>。OCR 原生库和临时工作文件仍使用 <code>%LOCALAPPDATA%\\KillerPDF</code>。",
    "所有 OCR 语言模型，包括英语，都在首次使用时下载并保留在缓存中。Tesseract 原生库会解压到各版本的 OCR 缓存。",
    "清除所有数据会尽可能移除设置、OCR 模型和临时文件。被锁定的文件可能保留。安装版保留签名和日志；便携版也会移除 <code>KillerPDF-Data</code> 文件夹。原始 PDF 不会被删除。",
    "结构编辑通过 The KillerPDF.Engine 写入临时 PDF 并重新打开，必要时恢复页面旋转和尚未保存的注释。原始文件仅在保存时更改。",
    "注释位置使用从左上角量起、不随缩放改变的 render-dim 单位。PDF 坐标在读写内容时转换，所有视图模式共用相同位置。",
    "历史标准验证",
    "源代码与测试数量请见概览。Corpus 结果会标明测试的构建版本与输入。qpdf、veraPDF 和 OpenSSL 分别检查结构、标准和签名。",
    "确认文本",
    "打开和修复 PDF"
  ],
  "bn": [
    "পোর্টেবল সংস্করণ সেটিংস, স্বাক্ষর ও OCR মডেল লঞ্চারের পাশের <code>KillerPDF-Data</code> ফোল্ডারে রাখে। নেটিভ OCR লাইব্রেরি ও অস্থায়ী কাজের ফাইল এখনো <code>%LOCALAPPDATA%\\KillerPDF</code> ব্যবহার করে।",
    "ইংরেজিসহ সব OCR ভাষার মডেল প্রথম ব্যবহারে ডাউনলোড হয় এবং ক্যাশে থাকে। নেটিভ Tesseract লাইব্রেরি প্রতিটি সংস্করণের OCR ক্যাশে বের করা হয়।",
    "সব ডেটা মুছুন সম্ভব হলে সেটিংস, OCR মডেল ও অস্থায়ী ফাইল সরায়। লক করা ফাইল থেকে যেতে পারে। ইনস্টল করা সংস্করণ স্বাক্ষর ও লগ রেখে দেয়; পোর্টেবল সংস্করণ <code>KillerPDF-Data</code> ফোল্ডারও মুছে দেয়। মূল PDF মুছে ফেলা হয় না।",
    "কাঠামোগত পরিবর্তন The KillerPDF.Engine দিয়ে একটি অস্থায়ী PDF লিখে আবার খোলে। প্রয়োজনে পাতার ঘূর্ণন ও অসংরক্ষিত টীকা ফিরিয়ে আনে। সংরক্ষণ করলেই মূল ফাইল বদলায়।",
    "টীকার অবস্থান উপরের বাঁ কোণ থেকে মাপা render-dim এককে থাকে, যা জুমে বদলায় না। PDF স্থানাঙ্ক পড়া বা লেখার সময় রূপান্তরিত হয় এবং সব ভিউতে একই অবস্থান ব্যবহৃত হয়।",
    "আগের মান যাচাই",
    "উৎস ফাইল ও পরীক্ষার সংখ্যা সারসংক্ষেপে আছে। Corpus ফলাফল পরীক্ষিত বিল্ড ও ইনপুট চিহ্নিত করে। qpdf, veraPDF ও OpenSSL আলাদাভাবে কাঠামো, মান ও স্বাক্ষর পরীক্ষা করে।",
    "লেখা নিশ্চিত করুন",
    "PDF খুলুন ও মেরামত করুন"
  ],
  "sv": [
    "Portabla kopior sparar inställningar, sparade signaturer och OCR-modeller i <code>KillerPDF-Data</code> bredvid startprogrammet. Inbyggda OCR-bibliotek och tillfälliga arbetsfiler använder fortfarande <code>%LOCALAPPDATA%\\KillerPDF</code>.",
    "Alla OCR-språkmodeller, även engelska, laddas ned första gången de används och ligger sedan kvar i cachen. Inbyggda Tesseract-bibliotek packas upp i den versionsbundna OCR-cachen.",
    "Rensa alla data tar bort sparade inställningar, OCR-modeller och tillfälliga filer där det går. Låsta filer kan finnas kvar. Installerade kopior behåller sparade signaturer och loggar; portabla kopior tar även bort mappen <code>KillerPDF-Data</code>. Dina käll-PDF:er raderas inte.",
    "Strukturella ändringar skriver och öppnar en tillfällig PDF på nytt via The KillerPDF.Engine. Sidrotationer och väntande anteckningar återställs vid behov. Originalfilen ändras först när du sparar.",
    "Anteckningarnas positioner använder render-dim-enheter som mäts från det övre vänstra hörnet och inte påverkas av zoom. PDF-koordinater konverteras när innehåll läses eller skrivs; samma positioner används i alla visningslägen.",
    "Tidigare standardvalidering",
    "Exakta antal källfiler och tester finns i Översikt. Korpusresultaten anger vilken version och vilka indata som testades. qpdf, veraPDF och OpenSSL gör separata kontroller av struktur, standarder och signaturer.",
    "Spara text",
    "Öppna och reparera PDF:er"
  ],
  "ko": [
    "포터블 버전은 설정, 저장된 서명, OCR 모델을 실행 파일 옆의 <code>KillerPDF-Data</code>에 저장합니다. 네이티브 OCR 라이브러리와 임시 작업 파일은 계속 <code>%LOCALAPPDATA%\\KillerPDF</code>를 사용합니다.",
    "영어를 포함한 모든 OCR 언어 모델은 처음 사용할 때 다운로드되어 캐시에 남습니다. 네이티브 Tesseract 라이브러리는 버전별 OCR 캐시에 압축 해제됩니다.",
    "모든 데이터 지우기는 저장된 설정, OCR 모델, 임시 파일을 가능한 한 제거합니다. 잠긴 파일은 남을 수 있습니다. 설치된 버전은 저장된 서명과 로그를 유지하고, 포터블 버전은 <code>KillerPDF-Data</code> 폴더도 제거합니다. 원본 PDF는 삭제되지 않습니다.",
    "구조 편집은 The KillerPDF.Engine을 통해 임시 PDF를 쓰고 다시 엽니다. 필요하면 페이지 회전과 대기 중인 주석을 복원합니다. 원본 파일은 저장할 때만 바뀝니다.",
    "주석 위치는 왼쪽 위에서 측정하며 확대/축소에 영향을 받지 않는 render-dim 단위를 사용합니다. PDF 좌표는 콘텐츠를 읽거나 쓸 때 변환되며, 모든 보기 모드에서 같은 위치를 사용합니다.",
    "이전 표준 검증",
    "정확한 소스 및 테스트 수는 개요를 참조하세요. 코퍼스 결과는 테스트한 빌드와 입력을 표시합니다. qpdf, veraPDF, OpenSSL이 구조, 표준, 서명을 각각 따로 검사합니다.",
    "텍스트 저장",
    "PDF 열기 및 복구"
  ]
};
  var keys = ['pt_47', 'pt_49', 'ph_238', 'pt_107', 'pt_74', 'pt_223', 'engine_evidence', 'kb_commit_text', 'f_open_t'];
  window.KPDF_SITE_INFO = {
    text: function (language, key) {
      var index = keys.indexOf(key);
      if (index < 0) return null;
      return (copy[language] || copy.en)[index];
    }
  };
  var backend = {
  "sl": "Razvojna veja 1.9 uporablja The KillerPDF.Engine za izris in izločanje besedila. Tesseract ostaja na voljo kot nadomestna možnost OCR. Trenutni API-ji pogona so opisani v vodniku za razvijalce.",
  "ru": "Ветка разработки 1.9 использует The KillerPDF.Engine для рендеринга и извлечения текста. Tesseract остается резервным движком OCR. Актуальные API движка описаны в руководстве для разработчиков.",
  "it": "Il ramo di sviluppo 1.9 usa The KillerPDF.Engine per il rendering e l’estrazione del testo. Tesseract rimane disponibile come motore OCR di riserva. La guida per sviluppatori descrive le API attuali del motore.",
  "vi": "Nhánh phát triển 1.9 dùng The KillerPDF.Engine để kết xuất và trích xuất văn bản. Tesseract vẫn là bộ máy OCR dự phòng. Hướng dẫn dành cho nhà phát triển mô tả các API hiện tại của bộ máy.",
  "hu": "Az 1.9-es fejlesztési ág a The KillerPDF.Engine motort használja a megjelenítéshez és a szöveg kinyeréséhez. A Tesseract továbbra is tartalék OCR-motorként érhető el. A fejlesztői útmutató ismerteti a motor jelenlegi API-jait.",
  "pl": "Gałąź rozwojowa 1.9 używa The KillerPDF.Engine do renderowania i wyodrębniania tekstu. Tesseract pozostaje zapasowym silnikiem OCR. Aktualne API silnika opisano w przewodniku dla programistów.",
  "cs": "Vývojová větev 1.9 používá The KillerPDF.Engine pro vykreslování a získávání textu. Tesseract zůstává k dispozici jako záložní modul OCR. Aktuální API modulu popisuje příručka pro vývojáře.",
  "es": "La rama de desarrollo 1.9 usa The KillerPDF.Engine para renderizar y extraer texto. Tesseract sigue disponible como motor OCR de respaldo. La guía para desarrolladores describe las API actuales del motor.",
  "de": "Der Entwicklungszweig 1.9 verwendet The KillerPDF.Engine zum Rendern und zur Textextraktion. Tesseract bleibt als OCR-Ersatz verfügbar. Das Entwicklerhandbuch beschreibt die aktuellen APIs der Engine.",
  "fr": "La branche de développement 1.9 utilise The KillerPDF.Engine pour le rendu et l’extraction de texte. Tesseract reste disponible comme moteur OCR de secours. Le guide des développeurs décrit les API actuelles du moteur.",
  "tr": "1.9 geliştirme dalı, görüntü oluşturma ve metin çıkarma için The KillerPDF.Engine kullanır. Tesseract yedek OCR motoru olarak kullanılabilir. Geliştirici kılavuzu motorun güncel API’lerini açıklar.",
  "zh": "1.9 開發分支使用 The KillerPDF.Engine 進行繪製和文字擷取。Tesseract 仍可作為備用 OCR 引擎。開發者指南說明了引擎目前的 API。",
  "zh-cn": "1.9 开发分支使用 The KillerPDF.Engine 进行渲染和文本提取。Tesseract 仍可作为备用 OCR 引擎。开发者指南介绍了引擎当前的 API。",
  "bn": "1.9 উন্নয়ন শাখা রেন্ডারিং ও লেখা নিষ্কাশনের জন্য The KillerPDF.Engine ব্যবহার করে। বিকল্প OCR ইঞ্জিন হিসেবে Tesseract রাখা হয়েছে। ইঞ্জিনের বর্তমান API-গুলো ডেভেলপার নির্দেশিকায় বর্ণনা করা হয়েছে।",
  "ja": "1.9 開発ブランチでは、描画とテキスト抽出に The KillerPDF.Engine を使用します。Tesseract は代替 OCR エンジンとして引き続き利用できます。現在のエンジン API については開発者ガイドを参照してください。",
  "kk": "1.9 әзірлеу тармағы беттерді бейнелеу және мәтінді шығару үшін The KillerPDF.Engine қолданады. Tesseract қосалқы OCR қозғалтқышы ретінде қолжетімді. Қозғалтқыштың қазіргі API-лері әзірлеуші нұсқаулығында сипатталған.",
  "uk": "Гілка розробки 1.9 використовує The KillerPDF.Engine для рендерингу та видобування тексту. Tesseract залишається резервним рушієм OCR. Актуальні API рушія описано в посібнику для розробників.",
  "nb": "Utviklingsgrenen 1.9 bruker The KillerPDF.Engine til gjengivelse og tekstuttrekk. Tesseract beholdes som en reserve for OCR. Utviklerveiledningen beskriver motorens gjeldende API-er.",
  "pt": "O ramo de desenvolvimento 1.9 usa The KillerPDF.Engine para renderização e extração de texto. O Tesseract continua disponível como mecanismo de OCR alternativo. O guia para desenvolvedores descreve as APIs atuais do mecanismo.",
  "sv": "Utvecklingsgrenen 1.9 använder The KillerPDF.Engine för rendering och textextrahering. Tesseract finns kvar som reserv för OCR. Utvecklarguiden beskriver motorns aktuella API:er.",
  "ko": "1.9 개발 브랜치는 렌더링과 텍스트 추출에 The KillerPDF.Engine을 사용합니다. Tesseract는 OCR 대체 엔진으로 계속 사용할 수 있습니다. 현재 엔진 API는 개발자 가이드를 참조하세요.",
  "en": "The 1.9 development branch uses The KillerPDF.Engine for rendering and text extraction. Tesseract remains available as an OCR fallback. See the developer guide for current engine APIs."
};
  var publicLabels = {
  "sl": "PDF-jev za prenos",
  "en": "downloadable PDFs",
  "es": "PDF descargables",
  "de": "herunterladbare PDFs",
  "fr": "PDF téléchargeables",
  "it": "PDF scaricabili",
  "pt": "PDFs para download",
  "nb": "nedlastbare PDF-er",
  "pl": "PDF-y do pobrania",
  "cs": "PDF ke stažení",
  "hu": "letölthető PDF-ek",
  "tr": "indirilebilir PDF'ler",
  "ru": "PDF для загрузки",
  "uk": "PDF для завантаження",
  "kk": "жүктеп алуға болатын PDF",
  "vi": "PDF có thể tải xuống",
  "ja": "ダウンロード可能なPDF",
  "zh": "可下載的 PDF",
  "zh-cn": "可下载的 PDF",
  "bn": "ডাউনলোডযোগ্য PDF",
  "sv": "nedladdningsbara PDF:er",
  "ko": "다운로드 가능한 PDF"
};
  var coverCopy = {
  "sl": "Prekrivni pravokotnik vizualno skrije izvirne besede. Ne odstrani spodnjega besedila ali slike. Prilagodite njegovo velikost, da povsem prekrije stare besede.",
  "en": "The cover hides the original words visually. It does not remove the underlying text or image. Resize it until the old words are fully covered.",
  "es": "La cubierta oculta las palabras originales visualmente. No elimina el texto ni la imagen subyacentes. Ajuste su tamaño hasta cubrir todas las palabras.",
  "de": "Die Abdeckung verdeckt die ursprünglichen Wörter optisch. Sie entfernt den darunterliegenden Text oder das Bild nicht. Ändern Sie ihre Größe, bis alle Wörter verdeckt sind.",
  "fr": "Le cache masque visuellement les mots d'origine. Il ne supprime pas le texte ou l'image sous-jacents. Redimensionnez-le pour couvrir tous les mots.",
  "it": "La copertura nasconde visivamente le parole originali. Non rimuove il testo o l'immagine sottostante. Ridimensionala fino a coprire tutte le parole.",
  "pt": "A cobertura oculta visualmente as palavras originais. Ela não remove o texto nem a imagem subjacentes. Ajuste o tamanho até cobrir todas as palavras.",
  "nb": "Dekselet skjuler de opprinnelige ordene visuelt. Det fjerner ikke teksten eller bildet under. Endre størrelsen til alle ordene er dekket.",
  "pl": "Osłona wizualnie zakrywa pierwotne słowa. Nie usuwa tekstu ani obrazu znajdującego się pod nią. Zmień jej rozmiar, aby zakryć wszystkie słowa.",
  "cs": "Kryt původní slova pouze vizuálně zakrývá. Neodstraňuje text ani obrázek pod ním. Změňte jeho velikost, aby zakryl všechna slova.",
  "hu": "A fedőfelület csak vizuálisan takarja el az eredeti szavakat. Nem távolítja el az alatta lévő szöveget vagy képet. Méretezze át, amíg minden szót eltakar.",
  "tr": "Kapak, özgün sözcükleri görsel olarak gizler. Alttaki metni veya görüntüyü kaldırmaz. Tüm sözcükleri kapatacak şekilde boyutlandırın.",
  "ru": "Покрытие визуально скрывает исходные слова. Оно не удаляет текст или изображение под ним. Измените размер, чтобы закрыть все слова.",
  "uk": "Покриття візуально приховує початкові слова. Воно не видаляє текст чи зображення під ним. Змініть розмір, щоб закрити всі слова.",
  "kk": "Қақпақ бастапқы сөздерді тек көзге көрінбейтін етеді. Ол астындағы мәтінді немесе суретті жоймайды. Барлық сөздерді жабатындай өлшемін өзгертіңіз.",
  "vi": "Lớp phủ chỉ che các từ gốc về mặt hiển thị. Nó không xóa văn bản hoặc hình ảnh bên dưới. Đổi kích thước để che kín tất cả các từ.",
  "ja": "カバーは元の文字を見た目上隠すだけで、下のテキストや画像を削除しません。すべての文字が隠れるようにサイズを調整してください。",
  "zh": "遮蓋框只會在視覺上隱藏原有文字，不會移除底下的文字或影像。請調整大小，直到完全遮住原有文字。",
  "zh-cn": "遮盖框只会在视觉上隐藏原有文字，不会移除底下的文字或图像。请调整大小，直到完全遮住原有文字。",
  "bn": "আবরণটি মূল শব্দগুলো শুধু দৃশ্যত ঢেকে রাখে। এটি নিচের লেখা বা ছবি মুছে ফেলে না। সব শব্দ ঢাকা না পড়া পর্যন্ত এর আকার পরিবর্তন করুন।",
  "sv": "Täckrutan döljer de ursprungliga orden visuellt. Den tar inte bort texten eller bilden under. Ändra storleken tills de gamla orden är helt täckta.",
  "ko": "덮개는 원래 단어를 화면에서만 가립니다. 아래에 있는 텍스트나 이미지는 제거하지 않습니다. 이전 단어가 완전히 가려질 때까지 크기를 조절하세요."
};
  var development = window.KPDF_ENGINE_FACTS && /^1\.9\./.test(window.KPDF_ENGINE_FACTS.version);
  window.KPDF_SITE_INFO.development = !!development;
  window.KPDF_SITE_INFO.backend = function (language) { return backend[language] || backend.en; };
  Object.keys(window.I18N || {}).forEach(function (language) {
    var strings = window.I18N[language];
    keys.forEach(function (key) { strings[key] = window.KPDF_SITE_INFO.text(language, key); });
    strings.ph_36 = '<code>Ctrl+</code> / <code>Ctrl-</code>: ' + strings.ph_36;
    strings.pt_144 = '<code>Ctrl+</code> / <code>Ctrl-</code>: ' + strings.pt_144;
    strings.ph_55 = coverCopy[language] || coverCopy.en;
    strings.pt_53 = strings.ph_238;
    if (strings.ph_46.indexOf('<code>Ctrl+Enter</code>') < 0) strings.ph_46 = strings.ph_46.replace(/<code>Enter<\/code>/g, '<code>Ctrl+Enter</code>');
    strings.ph_317 = strings.ph_317.replace(/KillerPDF\.exe/g, 'KillerPDF.App.exe');
    Object.keys(strings).forEach(function (key) {
      if (/^ph_/.test(key) && typeof strings[key] === 'string') {
        strings[key] = strings[key].replace(/KillerPDF\.exe (?=--)/g, '.\\KillerPDF.App.exe ');
      }
    });
    var clearList = strings.ph_237.indexOf('<b>');
    if (clearList >= 0) strings.ph_237 = strings.ph_237.slice(clearList);
    strings.tech_runtime_desc = strings.ph_309 + ' ' + strings.ph_310;
    strings.pt_150 = strings.pt_150.replace(/16([\s.,\u00A0]?)384/g, function (_, separator) { return '32' + separator + '768'; }).replace(/১৬,৩৮৪/g, '৩২,৭৬৮');
    strings.corpus_stat_public = publicLabels[language];
    strings.f_ocr_d = strings.f_ocr_d.replace(/[^.!?。！？।]*Tesseract[^.!?。！？।]*[.!?。！？।]?\s*$/, '').trim();
    strings.f_ocr_d += ' ' + strings.pt_49.split(/[.!。।]\s*/)[0] + '.';
    if (development) {
      ['b2_d', 'f_view_d_172', 'pa_10'].forEach(function (key) {
        strings[key] = strings[key].replace(/PDFium/g, 'The KillerPDF.Engine');
      });
      strings.pt_23 = 'The KillerPDF.Engine';
      strings.pt_25 = 'The KillerPDF.Engine';
      strings.pt_31 = 'The KillerPDF.Engine; Tesseract 5.2';
    }
  });
})();
