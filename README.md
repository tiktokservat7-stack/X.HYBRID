# X.HYBRID V1

Launcher Fortnite pour serveur privé (LawinServer), interface noir / bleu électrique,
session Epic persistante et **mises à jour automatiques** depuis les releases de ce dépôt.

![Accueil X.HYBRID V1](docs/home.png)

## Télécharger

[**Dernière release**](https://github.com/tiktokservat7-stack/X.HYBRID/releases/latest) →
`X.HYBRID V1.exe`.

- **Un seul fichier** : aucune DLL à côté, et **rien d'autre à installer** — le serveur
  privé et le moteur Node.js sont embarqués dans l'exe et s'installent tout seuls au
  premier lancement (`%APPDATA%\FNHybrid\server` et `%APPDATA%\FNHybrid\node`).
- Nécessite **.NET Framework 4.8** (présent sur Windows 10/11). Node.js n'est **pas**
  nécessaire : sa version embarquée est utilisée.
- L'application demande l'élévation administrateur (fichier `hosts` + proxy SSL).
- Exe non signé : SmartScreen peut demander « Informations complémentaires » →
  « Exécuter quand même ».

Au lancement, le launcher vérifie les releases GitHub. Si une version plus récente
existe, il propose de l'installer, affiche la progression, se ferme, remplace son exe,
redémarre la nouvelle version et supprime l'ancienne. **La session Epic est conservée.**

![Mise à jour](docs/update.png)

## Fonctionnalités

- Demande de connexion au démarrage quand aucune session n'est enregistrée.
- Connexion Epic par code d'activation (aucun mot de passe stocké).
- Jetons de session chiffrés avec DPAPI, restauration automatique au démarrage.
- Lancement de Fortnite via Epic Games Launcher, en ligne ou en mode privé.
- Proxy SSL local + redirection `hosts` vers LawinServer.
- Profil du serveur privé : V-Bucks, niveau, pseudo.
- Console intégrée, notifications Windows.
- Un seul exe, mises à jour GitHub.

## Compiler

Le contenu embarqué (serveur + moteur Node.js) se fabrique d'abord :

```bat
powershell -ExecutionPolicy Bypass -File tools\make-payload.ps1
cd FNHybridLauncher
dotnet build FNHybridLauncher.csproj -c Release -o bin/staging
```

`make-payload.ps1` produit `FNHybridLauncher/payload.zip` (embarqué dans l'exe) et
`dist/serveur-source.zip` (sources du serveur, à joindre aux releases). Le fichier
produit est `X.HYBRID V1.exe` (~54 Mo). Contrôles rapides :

```bat
"X.HYBRID V1.exe" --self-test          :: chiffrement + contenu embarqué (0 = OK)
"X.HYBRID V1.exe" --install-payload    :: installe le serveur sans ouvrir la fenêtre
```

## Données locales

```
%APPDATA%\FNHybrid\        comptes, préférences, journaux
%APPDATA%\FNHybrid\server\ serveur privé installé automatiquement
%APPDATA%\FNHybrid\node\   moteur Node.js embarqué
```

Aucun jeton n'est écrit dans le dépôt ni dans le dossier de l'application.
La variable d'environnement `XHYBRID_HOME` permet de déplacer tout ce dossier
(profil séparé, utilisé par les tests).

## Serveur privé

Le serveur est [LawinServer](https://github.com/Lawin0129/LawinServer) (GPLv3),
adapté pour X.HYBRID et **embarqué dans l'exe** : au premier besoin, il est
extrait dans `%APPDATA%\FNHybrid\server` avec le moteur Node.js. Les profils, la
configuration et les données du joueur ne sont jamais écrasés lors d'une mise à
jour. Un dossier `LawinServer` placé à côté du launcher reste utilisé en priorité
(pratique pour développer le serveur).

## Mises à jour

Voir [HEBERGEMENT.md](HEBERGEMENT.md) : dépôt, tags, release, sécurité.

## Avertissement

Projet communautaire non affilié à Epic Games. Utilise ton propre compte et ton
propre serveur. Les marques citées appartiennent à leurs propriétaires.
