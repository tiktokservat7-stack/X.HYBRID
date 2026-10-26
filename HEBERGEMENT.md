# X.HYBRID V1 — hébergement et mises à jour

## Ce qu'il faut héberger

Le fichier **`X.HYBRID V1.exe`** (asset de la release) suffit : c'est **un seul exe**
(BouncyCastle est embarqué dedans, aucune DLL à côté). Il nécessite
**.NET Framework 4.8**, présent d'origine sur Windows 10 et 11.

Téléchargement : https://github.com/tiktokservat7-stack/X.HYBRID/releases/latest

L'application demande l'élévation administrateur (fichier hosts + proxy SSL).
Windows SmartScreen peut afficher un avertissement sur un exe non signé :
« Informations complémentaires » → « Exécuter quand même ».

Test rapide du fichier seul (retourne 0 si le chiffrement embarqué fonctionne) :

```bat
"X.HYBRID V1.exe" --self-test
```

## Dépôt GitHub utilisé

Par défaut : **`tiktokservat7-stack/X.HYBRID`**
(https://github.com/tiktokservat7-stack/X.HYBRID). Pour un autre dépôt, deux solutions sans
recompiler :

1. déposer à côté de l'exe un fichier `updates.txt` contenant une seule ligne,
   par exemple `mon-compte/mon-repo` (ou l'URL complète du dépôt) ;
2. ou définir la variable d'environnement `XHYBRID_UPDATE_REPO`.

Sinon, modifier `DefaultRepo` dans
[UpdateService.cs](FNHybridLauncher/UpdateService.cs) avant de compiler.

## Publier une version (exemple V1.1)

1. Dans [AppInfo.cs](FNHybridLauncher/AppInfo.cs), incrémenter :
   - `Version = "1.1"` (affiché « V1.1 »)
   - `VersionTag = "v1.1.0"` (comparé aux tags GitHub)
2. Compiler :

   ```bat
   cd FNHybridLauncher
   dotnet build FNHybridLauncher.csproj -c Release -o bin/staging
   ```

3. Sur GitHub : **Releases → Draft a new release**
   - tag : `v1.1.0` (doit être **supérieur** au tag de la version installée) ;
   - joindre l'exe produit (`X.HYBRID V1.exe`). N'importe quel fichier `.exe`
     fonctionne ; ceux contenant « x.hybrid » dans le nom sont préférés ;
   - publier la release (pas en brouillon).

Au lancement suivant, l'application interroge
`https://api.github.com/repos/<dépôt>/releases/latest` :

1. fenêtre « Mise à jour disponible » avec les notes de la release
   (« Installer maintenant » / « Plus tard ») ;
2. téléchargement avec barre de progression ;
3. fermeture, remplacement de l'exe, redémarrage automatique ;
4. suppression de l'ancienne version (`X.HYBRID V1.exe.old`) par le script.

La vérification est silencieuse si le dépôt n'existe pas, si Internet est coupé
ou si la version est à jour. Aucune fenêtre ne s'ouvre pendant une partie ou
pendant une connexion Epic.

## Session conservée

Les comptes, jetons DPAPI et préférences sont dans :

```
%APPDATA%\FNHybrid\
```

La mise à jour ne touche jamais ce dossier : après redémarrage, la session Epic
est restaurée comme avant.

## Sécurité

- Le dépôt doit être **public** (l'API GitHub est lue sans jeton).
- Toute personne ayant le droit de publier une release sur ce dépôt peut
  distribuer un exe : active la double authentification et limite les
  collaborateurs.
- L'exe téléchargé est vérifié comme exécutable Windows (en-tête MZ, taille
  minimale) mais **n'est pas signé**. Si tu veux empêcher toute substitution,
  signe les exes (certificat de signature de code) et vérifie la signature
  avant publication.
- Ne mets jamais de jeton GitHub dans l'exe : il serait lisible par tous.

## Rappel des fichiers

- `FNHybridLauncher/AppInfo.cs` — nom, version affichée, tag comparé.
- `FNHybridLauncher/UpdateService.cs` — dépôt, vérification, téléchargement,
  script de remplacement.
- `FNHybridLauncher/UpdateWindow.cs` — fenêtre de proposition/progression.
- `dist/X.HYBRID V1.exe` — fichier produit en local, joint à la release GitHub.
