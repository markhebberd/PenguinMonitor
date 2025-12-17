# PenguinMonitor Implementation Plan

Implementation details for the database schema defined in `Proposed_DB_Schema.md`.

---

## PHP API Endpoints

### Observer Management
| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/observers.php` | POST | Register new observer (name, email, passphrase) |
| `/api/observers.php?action=login` | POST | Validate credentials, returns observer details |

Passphrases hashed with `password_hash()` (bcrypt), verified with `password_verify()`.

### Colony Management
| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/regions.php` | GET | List all regions |
| `/api/colonies.php?region_id=X` | GET | List colonies for region |
| `/api/colonies.php?colony_id=X` | GET | Get colony details |

### Observation Data
| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/observations.php` | POST | Batch upload observations with scans |
| `/api/observations.php?colony_id=X` | GET | Download colony observations (last 12 months) |

Batch upload auto-creates `observation_locations` and `penguin_scans` as needed.

### Penguin Data
| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/penguins.php` | GET | Download penguin master list |
| `/api/penguins.php` | POST | Create/update penguin records |
| `/api/penguin_biometrics.php` | POST | Upload penguin biometrics |
| `/api/penguin_biometrics.php?penguin_id=X` | GET | Get penguin biometric history |

### Location Management
| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/locations.php?colony_id=X` | GET | Get all locations for colony |
| `/api/locations.php` | POST | Create/update location |

---

## Data Sync Strategy

### Upload
- App uploads all monitoring data at end of session (batch)
- Server uses transactions (all-or-nothing per observation)
- Idempotent: checks for duplicates by (location_id, observation_time_utc, observer_id)

### Download
- App downloads last 12 months of observations for selected colony
- Server data is authoritative (app downloads latest on sync)

### Offline Handling
- Local JSON files saved immediately (existing behaviour)
- Track `uploaded_to_server` flag per observation
- On partial failure, retry only unsynced observations
- Local files remain authoritative until server confirms

---

## Implementation Phases

### Phase 1: Database Setup
1. Create SQL migration script for all tables
2. Add sample data for Nelson/Tarakohe colony
3. Migrate existing box_tags data

### Phase 2: PHP API
1. `observers.php` — registration and login
2. `regions.php` — region list
3. `colonies.php` — colony list and details
4. `observations.php` — batch upload and download
5. `penguins.php` — penguin master list
6. `penguin_biometrics.php` — biometric data
7. Update `boxtags.php` for colony_id filtering

### Phase 3: C# Client
1. Create models matching database schema
2. Create API service classes
3. Add observer login UI in SettingsPage
4. Add colony selection UI in SettingsPage
5. Update DataStorageService for sync
6. Implement batch upload/download logic
7. Remove Google Sheets CSV dependency

### Phase 4: Data Migration
1. Script to upload existing JSON data
2. Test with historical monitoring sessions
3. Verify data integrity

---

## Files to Create

### Server (PHP)
- `ServerApi/observers.php`
- `ServerApi/regions.php`
- `ServerApi/colonies.php`
- `ServerApi/observations.php`
- `ServerApi/penguins.php`
- `ServerApi/penguin_biometrics.php`
- `ServerApi/database_schema.sql`

### Client (C#)
- `Services/ObserverApiService.cs`
- `Services/ColonyApiService.cs`
- `Services/ObservationApiService.cs`
- `Services/PenguinApiService.cs`
- `Models/Observer.cs`
- `Models/Colony.cs`
- `Models/Region.cs`
- `Models/ObservationLocation.cs`

### Files to Update
- `ServerApi/boxtags.php` — add colony_id support
- `Services/DataStorageService.cs` — add sync methods
- `Pages/SettingsPage.xaml` — colony selection UI
- `Services/DataManager.cs` — track selected colony

---

## Data Migration Notes

### Tag Number Format
- Current app uses 8-character IDs (last 8 chars of scanned tag)
- Database stores full 17-character ISO format
- Migration script maps 8-char to 17-char using source data

### Historical Data Defaults
- Create "unknown" observer for pre-system data
- Assign to colony based on location_sets_string match

### Legacy System
- Existing `Backend.RequestServerResponse()` system is deprecated
- No transition period — replaced entirely
- No sync between old and new systems
