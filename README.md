# USB Music Prep

Application de bureau Avalonia / .NET 10 pour préparer une bibliothèque musicale sur une clé USB, sous Linux et Windows.

## Parcours principal

**Source + destination USB → Analyser → Vérifier → Préparer l’USB → Bilan**

1. **Source** : choisir le dossier de musique ou coller son chemin. Les originaux restent inchangés pendant la préparation USB.
2. **Destination USB** : choisir le dossier de destination. Les deux dossiers doivent être distincts et non imbriqués.
3. **Paramètres** : vérifier le profil affiché. Les formats cochés sont copiés sans réencodage ; les autres sont convertis directement vers la destination.
4. **Analyser** : l’application analyse la source et l’USB, puis compare les pistes à partir des tags locaux. Aucune écriture n’a lieu à cette étape.
5. **Vérifier** : consulter le résumé et la colonne **État USB** :
   - **À ajouter** : piste absente de l’USB et admissible à la préparation ;
   - **Présent sur l’USB** : piste déjà identifiée sur la destination ;
   - **Doublon source** : autre fichier portant la même identité de piste ;
   - **Tags inconnus** : identité de piste inexploitable.
6. Par défaut, toutes les pistes **À ajouter** sont préparées. Pour un sous-ensemble, sélectionner les lignes avec Ctrl/Shift puis cocher **Limiter à la sélection du tableau**. Le résumé indique le nombre réellement admissible : les pistes présentes et les doublons restent exclus.
7. **Préparer l’USB** : confirmer la destination, la portée et le nombre de copies/conversions. FFmpeg et le codec choisi sont vérifiés avant toute conversion ; aucune piste à convertir n’est ignorée silencieusement faute de FFmpeg.
8. Consulter le **bilan** : copiées, converties, ignorées, échecs ou interruption. La comparaison USB est ensuite actualisée ; le bilan reste visible séparément.

Changer un dossier ou enregistrer un profil invalide la comparaison : il faut **Analyser** à nouveau. Utiliser **Réanalyser** après des modifications externes des tags ou du contenu des dossiers. Les actions concurrentes sont désactivées pendant le traitement.

## Métadonnées et organisation

- Formats analysés : MP3, M4A, FLAC, WAV, AAC, OGG, WMA, AIFF, ALAC.
- Comparaison par clé `artiste | album | titre`. En l’absence de tags exploitables, le nom de fichier peut servir de repli : la correspondance n’est alors pas garantie.
- Le résumé signale séparément les tags incomplets ; cela ne signifie pas nécessairement **Tags inconnus** dans le tableau.
- Le transfert organise les fichiers en `AlbumArtist/Album/nom-original` (l’artiste de piste, p. ex. « Weezer ft. Wednesday », ne crée donc plus de dossier séparé), ou `Album/nom-original` pour les albums « Various Artists » ou, sans album artist, les pistes à plusieurs interprètes. Il ne reproduit pas l’arborescence source.
- **Album artist absent** : avant toute écriture, l’application interroge l’API MusicBrainz (une recherche par album, 1 requête/s) et utilise l’artiste de la meilleure correspondance exacte (score ≥ 90). La valeur est inscrite dans la copie sur l’USB ; la source n’est jamais modifiée. Si un album est introuvable ou si le réseau échoue, une confirmation propose de continuer avec l’artiste de la piste.
- Les collisions de noms produisent un suffixe numérique, sans dialogue d’écrasement. Les pistes déjà identifiées sur l’USB ne sont pas recopiées.
- La conversion préserve les métadonnées locales via FFmpeg. Le traitement est séquentiel ; l’ordre physique final dépend aussi du système de fichiers et du lecteur.

**Picard n’est plus lancé par l’application.** La lecture des tags et le nommage local fonctionnent sans éditeur externe. MusicBrainz n’est utilisé que pour retrouver l’album artist manquant ; l’identification par empreinte (AcoustID, `MusicBrainzTagger.cs`) reste inactive.

## Paramètres

Configurer les formats à conserver, le format de sortie (MP3 ou M4A/AAC), le codec et le débit. Si aucun format de copie n’est coché, les valeurs MP3/M4A par défaut sont rétablies. Fraunhofer AAC est proposé uniquement si FFmpeg expose `libfdk_aac` ; sinon utiliser AAC natif.

Le profil est enregistré dans le dossier de données utilisateur `usb_prep/conversion_settings.json`. Une préférence de format ne garantit pas la compatibilité avec votre autoradio.

## Progression et arrêt

**Arrêter** interrompt l’analyse ou demande l’arrêt du transfert, et termine le processus FFmpeg actif. La progression du transfert est par fichier, pas un pourcentage précis à l’intérieur d’un long fichier. Une copie en cours finit avant l’arrêt ; les fichiers déjà terminés restent sur l’USB. Les sorties de conversion incomplètes sont supprimées si possible ; consulter **Logs FFmpeg** en cas d’erreur de nettoyage.

Les logs sont consultables à la demande, sans fenêtre imposée pendant la préparation principale.

## Outils avancés

Ces outils ne sont pas des étapes nécessaires du parcours principal :

- **Convertir les FLAC dans la source** : crée et organise les sorties dans la source à partir des tags locaux, en conservant les FLAC. Des sorties existantes peuvent être remplacées ; une confirmation spécifique avertit de ce comportement. Relancer l’analyse après utilisation.
- **USB : regrouper par album** : déplace les fichiers déjà sur la destination dans des dossiers Album, conserve leurs noms et nettoie les dossiers vides. Cette disposition est distincte de celle du transfert normal.
- **USB : nettoyer les doublons** : prévisualise les doublons identifiés par métadonnées avant suppression.
- **USB : inspecter et réparer** : prévisualise les corrections de disposition et d’ordre avant application.

Faire une sauvegarde avant maintenance. Les fonctions historiques de réorganisation ne sont pas toutes interruptibles à l’intérieur d’un déplacement ; l’arrêt n’est pas une restauration des modifications déjà réalisées.

## Installation et lancement

- .NET 10 SDK pour compiler et lancer depuis les sources.
- Windows ou Linux avec une session graphique.
- FFmpeg dans le PATH, nécessaire uniquement pour les formats qui doivent être convertis, avec l’encodeur choisi disponible.

Depuis le dépôt : `dotnet restore`, `dotnet build`, puis `dotnet run`.
Les scripts `launch-linux.sh` et `launch-windows.bat` lancent le projet en Debug.

## Dépannage

- **Conversion indisponible** : installer FFmpeg avec le codec demandé, changer de codec dans Paramètres ou conserver les formats concernés sans conversion.
- **Correspondances incorrectes** : compléter les tags locaux puis réanalyser. Les noms de fichiers seuls ne permettent pas une identification fiable.
- **Échecs de préparation** : vérifier permissions, espace libre et logs FFmpeg. Le bilan n’efface pas les copies réussies.
- **Sélecteur de dossiers Linux** : en cas d’échec du portail/D-Bus, un dialogue externe `zenity`, `qarma` ou `kdialog` est tenté.

Pour les autoradios, vérifier les formats acceptés et le système de fichiers (FAT32 ou exFAT selon le modèle), garder une profondeur de dossiers raisonnable et tester quelques albums avant une grosse préparation.

## Structure

- `MainWindow.axaml` / `MainWindow.axaml.cs` : parcours, analyse, comparaison, transfert et outils avancés.
- `SettingsWindow.axaml` / `SettingsWindow.axaml.cs`, `ConversionSettings.cs` : profil et détection des codecs.
- `FileOrganizer.cs` : réorganisation locale, déplacements et nettoyage.
- `FileNamer.cs` : nommage basé sur les tags et détection des compilations (aucun lancement Picard).
- `FfmpegConsoleWindow.axaml` / `FfmpegConsoleWindow.axaml.cs` : logs.
- `MusicBrainzTagger.cs` : code dormant, intégration future hors du parcours actuel.

## Licence

Projet open source utilisable à des fins personnelles et commerciales.