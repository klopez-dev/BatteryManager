Pour lancer l'application, il faut ouvrir le dossier "dist" et lancer le fichier .exe.

# Battery Manager

Application Windows native qui affiche les niveaux de batterie exposés par Windows.

## Construire le .exe

Dans PowerShell, depuis ce dossier :

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\build.ps1
.\dist\BatteryManager.exe
```

Le script utilise le compilateur .NET Framework fourni avec Windows. Aucun paquet externe n'est nécessaire.

## Ce qui est détecté

- Batterie interne du PC avec pourcentage réel et état secteur/batterie.
- Périphériques Bluetooth associés et visibles par Windows.
- Option « Lancer au démarrage de Windows » via la clé utilisateur `HKCU`.
- Actualisation manuelle et automatique toutes les 30 secondes.

Un périphérique Bluetooth peut apparaître avec `--` lorsque son fabricant ne publie pas le niveau de batterie dans Windows. C'est fréquent pour certains iPad/iPhone, Apple Watch, AirPods et enceintes. Cette limitation vient du profil Bluetooth exposé par l'appareil ; elle ne doit pas être remplacée par une valeur estimée.

## Évolution recommandée

Pour obtenir les pourcentages de ces accessoires, la prochaine étape est d'ajouter une couche par fabricant/protocole : Windows Runtime `DeviceInformation` et `GattDeviceService` pour les appareils BLE, puis des adaptateurs dédiés quand un fabricant utilise un protocole propriétaire.
