# PenguinMonitor MySQL Database Schema Plan

## Overview
Design and implement a complete MySQL database schema for PenguinMonitor to store all monitoring data on the shared hosting server. The schema supports multiple colonies/regions with shared data access among trusted users.

---

## Database Schema Design

### 1. Observers Table
| Column | Type | Constraints |
|--------|------|-------------|
| observer_id | INT | PRIMARY KEY AUTO_INCREMENT |
| observer_name | VARCHAR(100) | NOT NULL UNIQUE |
| email | VARCHAR(255) | |
| passphrase_hash | VARCHAR(255) | NOT NULL (bcrypt hash via password_hash()) |
| created_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP |
| updated_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP |

### 2. Regions Table
| Column | Type | Constraints |
|--------|------|-------------|
| region_id | INT | PRIMARY KEY AUTO_INCREMENT |
| region_name | VARCHAR(100) | NOT NULL UNIQUE |
| created_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP |
| updated_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP |

### 3. Colonies Table
| Column | Type | Constraints |
|--------|------|-------------|
| colony_id | INT | PRIMARY KEY AUTO_INCREMENT |
| region_id | INT | NOT NULL, FOREIGN KEY → regions(region_id) |
| colony_name | VARCHAR(100) | NOT NULL |
| box_sets_string | TEXT | AllBoxSetsString format: {1-150,AA-AC},{N1-N6} |
| created_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP |
| updated_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP |
| | | UNIQUE KEY (region_id, colony_name) |

### 4. Colony Permissions Table
| Column | Type | Constraints |
|--------|------|-------------|
| permission_id | INT | PRIMARY KEY AUTO_INCREMENT |
| colony_id | INT | NOT NULL, FOREIGN KEY → colonies(colony_id) |
| observer_id | INT | NOT NULL, FOREIGN KEY → observers(observer_id) |
| role | VARCHAR(20) | NOT NULL (admin, edit, view) |
| created_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP |
| | | UNIQUE KEY (colony_id, observer_id) |
| | | INDEX (observer_id) |

### 5. Observation Locations Table
| Column | Type | Constraints |
|--------|------|-------------|
| location_id | INT | PRIMARY KEY AUTO_INCREMENT |
| colony_id | INT | NOT NULL, FOREIGN KEY → colonies(colony_id) |
| location_name | VARCHAR(50) | NOT NULL (Box number, beach name, burrow ID, etc.) |
| location_type | VARCHAR(20) | DEFAULT 'box' (box, beach, burrow, rocky_area, etc.) |
| persistent_notes | TEXT | |
| rfid_tag_number | VARCHAR(50) | NULL (not all locations have RFID tags) |
| rfid_scan_time_utc | DATETIME | NULL |
| rfid_latitude | DOUBLE | NULL |
| rfid_longitude | DOUBLE | NULL |
| rfid_accuracy | FLOAT | NULL |
| created_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP |
| updated_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP |
| | | UNIQUE KEY (colony_id, location_name) |
| | | UNIQUE KEY (colony_id, rfid_tag_number) |
| | | INDEX (location_type) |

### 6. Observations Table (Box Visits)
| Column | Type | Constraints |
|--------|------|-------------|
| observation_id | INT | PRIMARY KEY AUTO_INCREMENT |
| location_id | INT | NOT NULL, FOREIGN KEY → observation_locations(location_id) |
| observer_id | INT | NOT NULL, FOREIGN KEY → observers(observer_id) |
| observation_time_utc | DATETIME | NOT NULL |
| adults | INT | DEFAULT 0 |
| eggs | INT | DEFAULT 0 |
| chicks | INT | DEFAULT 0 |
| breeding_chance | VARCHAR(50) | |
| gate_status | VARCHAR(50) | |
| notes | TEXT | |
| is_deleted | BOOLEAN | DEFAULT FALSE |
| deletion_reason | TEXT | NULL |
| deleted_at | TIMESTAMP | NULL |
| deleted_by | INT | NULL, FOREIGN KEY → observers(observer_id) |
| created_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP |
| updated_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP |
| | | INDEX (observation_time_utc) |
| | | INDEX (location_id, observation_time_utc) |
| | | INDEX (observer_id) |
| | | INDEX (is_deleted) |

### 7. Penguins Table (Master Penguin List)
| Column | Type | Constraints |
|--------|------|-------------|
| penguin_id | INT | PRIMARY KEY AUTO_INCREMENT |
| tag_number | VARCHAR(17) | NOT NULL UNIQUE |
| chip_date | DATE | |
| chipped_as_adult | BOOLEAN | DEFAULT FALSE |
| sex | VARCHAR(10) | |
| life_stage | VARCHAR(20) | |
| vid_for_scanner | VARCHAR(50) | |
| created_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP |
| updated_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP |

### 8. Penguin Scans Table (Links Penguins to Observations)
| Column | Type | Constraints |
|--------|------|-------------|
| scan_id | INT | PRIMARY KEY AUTO_INCREMENT |
| observation_id | INT | NOT NULL, FOREIGN KEY → observations(observation_id) ON DELETE CASCADE |
| penguin_id | INT | NOT NULL, FOREIGN KEY → penguins(penguin_id) |
| scan_time_utc | DATETIME | NOT NULL |
| latitude | DOUBLE | |
| longitude | DOUBLE | |
| accuracy | FLOAT | |
| created_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP |
| | | INDEX (observation_id) |
| | | INDEX (penguin_id) |

### 9. Penguin Observations Table
| Column | Type | Constraints |
|--------|------|-------------|
| stats_id | INT | PRIMARY KEY AUTO_INCREMENT |
| penguin_id | INT | NOT NULL, FOREIGN KEY → penguins(penguin_id) |
| observation_id | INT | FOREIGN KEY → observations(observation_id) ON DELETE SET NULL |
| observation_date | DATE | NOT NULL |
| weight | DECIMAL(6,2) | |
| sex | VARCHAR(10) | |
| left_flipper_length | DECIMAL(5,2) | |
| right_flipper_length | DECIMAL(5,2) | |
| body_length | DECIMAL(5,2) | |
| beak_length | DECIMAL(5,2) | |
| condition_healthy | BOOLEAN | DEFAULT FALSE |
| condition_underweight | BOOLEAN | DEFAULT FALSE |
| condition_ticks | BOOLEAN | DEFAULT FALSE |
| condition_dead | BOOLEAN | DEFAULT FALSE |
| condition_dog_attacked | BOOLEAN | DEFAULT FALSE |
| condition_attacked | BOOLEAN | DEFAULT FALSE |
| notes | TEXT | |
| created_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP |
| | | INDEX (penguin_id, observation_date) |

### 10. Audit Log Table (Track All Changes)
| Column | Type | Constraints |
|--------|------|-------------|
| audit_id | INT | PRIMARY KEY AUTO_INCREMENT |
| table_name | VARCHAR(50) | NOT NULL |
| record_id | INT | NOT NULL |
| action | VARCHAR(20) | NOT NULL (INSERT, UPDATE, DELETE, UNDELETE) |
| observer_id | INT | NOT NULL, FOREIGN KEY → observers(observer_id) |
| changed_fields | JSON | JSON object of field: {old, new} values |
| change_timestamp | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP |
| | | INDEX (table_name, record_id) |
| | | INDEX (observer_id) |
| | | INDEX (change_timestamp) |


---

## Key Design Decisions

### Multi-Colony Support
- **Shared Database**: All colonies in one database with colony_id foreign keys
- **Box Isolation**: Each colony can have "Box 1" without conflicts (unique constraint on colony_id + box_id)
- **Single API Key**: Simple authentication for trusted users, colony selection in app UI
- **Colony Switching**: App can download colony list and switch between them
- **Observer Authentication**: Each observer has their own passphrase (no complexity requirements)
- **Observer Tracking**: All observations linked to observer_id for accountability

### Data Relationships
- **Region → Colony**: One region contains many colonies (Nelson/Tarakohe, Oamaru/Inlet)
- **Colony → ObservationLocations**: Each colony has its own set of locations (boxes, beaches, burrows, etc.)
- **ObservationLocation → Observations**: Many observations per location over time
- **Observation → PenguinScans**: Each visit can have multiple penguin scans
- **Penguin → PenguinScans**: Track all locations a penguin was seen
- **Penguin → PenguinObservations**: Track growth/health over time

### Automatic Creation
- **ObservationLocations**: Created automatically when first observation is saved for a location_name
- **RFID Tags**: Stored in observation_locations (replaces separate box_tags table)
- **Penguins**: Created automatically when first scanned (from tag_number)
- **PersistentNotes**: Stored in observation_locations.persistent_notes field

### Audit Trail & Undo Support
- **Soft Deletes**: Observations marked as deleted (is_deleted=TRUE) not actually removed
- **Audit Log**: All INSERT/UPDATE/DELETE operations logged with observer_id and changed fields
- **Change Tracking**: JSON field stores before/after values for all modified fields
- **Undo Capability**: Can restore deleted observations or revert changes using audit log
- **Who Changed What**: Every modification traceable to specific observer
- **Delete Attribution**: deleted_by field tracks who deleted an observation

### Data Sync Strategy
- **Batch Upload**: App uploads all monitoring data at end of session
- **Historical Upload**: One-time upload of existing historical data
- **Filtered Download**: App downloads last 12 months of observations for selected colony
- **Shared Access**: All users can download colony dataset (last 12 months)
- **Conflict Resolution**: Server data is authoritative (app downloads latest)
- **Penguin Data Source**: SQL database is the source of truth (no more Google Sheets dependency)
- **Breeding Predictions**: Calculated client-side only (not stored in database)

### Offline Failure Handling
- **Local Persistence**: All observations saved to local JSON files immediately (existing behavior)
- **Upload Tracking**: Track which observations have been successfully uploaded (add `uploaded_to_server` boolean field)
- **Partial Upload Recovery**: If batch upload fails partway through:
  - Server uses database transactions (all-or-nothing per observation)
  - App marks individual observations as uploaded when confirmed by server
  - On next sync attempt, only upload observations where `uploaded_to_server = false`
- **Retry Logic**: App retries failed uploads on next sync attempt (user-initiated or automatic)
- **Upload Status UI**: Show user which data is synced vs pending upload
- **No Data Loss**: Local JSON files remain authoritative until server confirms upload
- **Idempotent Uploads**: Server checks for duplicate observations (same location_id + observation_time_utc + observer_id) before inserting

---

## PHP API Endpoints to Implement

### Observer Management
| Endpoint | Method | Description |
|----------|--------|-------------|
| /api/observers.php | POST | Register new observer (name, email, passphrase). Passphrase hashed with password_hash() before storing |
| /api/observers.php?action=login | POST | Validate observer credentials with password_verify() → Returns observer_id, observer_name, email |

### Colony Management
| Endpoint | Method | Description |
|----------|--------|-------------|
| /api/regions.php | GET | List all regions |
| /api/colonies.php?region_id=X | GET | List colonies for region |
| /api/colonies.php?colony_id=X | GET | Get colony details (including box_sets_string) |

### Observation Data
| Endpoint | Method | Description |
|----------|--------|-------------|
| /api/observations.php | POST | Batch upload observations (Colony ID + Observer ID + array of box observations with scans). Creates observation_locations, observations, and penguin_scans automatically |
| /api/observations.php?colony_id=X | GET | Download colony observations (last 12 months). Filters by observation_time_utc >= (NOW() - INTERVAL 12 MONTH) |

### Penguin Data (Source of Truth)
| Endpoint | Method | Description |
|----------|--------|-------------|
| /api/penguins.php | GET | Download complete penguin master list |
| /api/penguins.php | POST | Create/update penguin records (admin function) |
| /api/penguin_observations.php | POST | Upload penguin observations (weight, measurements, health) |
| /api/penguin_observations.php?penguin_id=X | GET | Get penguin observation history |

### Location Management
| Endpoint | Method | Description |
|----------|--------|-------------|
| /api/locations.php?colony_id=X | GET | Get all locations for colony (includes RFID tags if assigned) |
| /api/locations.php | POST | Create/update location (including RFID tag assignment) |

---

## Implementation Steps

### Phase 1: Database Setup
1. Create SQL migration script for all tables
2. Add sample data for Nelson/Tarakohe colony
3. Migrate existing box_tags table to include colony_id

### Phase 2: PHP API Development
1. Create observers.php endpoint (registration + login)
2. Create regions.php endpoint
3. Create colonies.php endpoint
4. Create observations.php endpoint with batch upload support
5. Create penguins.php endpoint (source of truth)
6. Create penguin_observations.php endpoint
7. Update boxtags.php to support colony_id filtering

### Phase 3: C# Client Integration
1. Create C# models matching database schema
2. Create API service classes for each endpoint
3. Add observer login UI in SettingsPage
4. Add colony selection UI in SettingsPage
5. Update DataStorageService to sync with server
6. Implement batch upload logic (upload all observations)
7. Implement download/sync logic (download complete colony history)
8. Remove Google Sheets CSV download dependency

### Phase 4: Data Migration
1. Script to upload existing JSON data to server
2. Test with historical monitoring sessions
3. Verify data integrity

---

## Critical Files

### New Files to Create
- ServerApi/observers.php - Observer registration and login
- ServerApi/regions.php - Region management
- ServerApi/colonies.php - Colony management
- ServerApi/observations.php - Observation data sync
- ServerApi/penguins.php - Penguin master list (source of truth)
- ServerApi/penguin_observations.php - Penguin observations (weight, measurements, health)
- ServerApi/database_schema.sql - Complete schema definition
- Services/ObserverApiService.cs - C# API client for observer operations
- Services/ColonyApiService.cs - C# API client for colony operations
- Services/ObservationApiService.cs - C# API client for observations
- Services/PenguinApiService.cs - C# API client for penguin data
- Models/Observer.cs - C# model for observer
- Models/Colony.cs - C# model for colony
- Models/Region.cs - C# model for region
- Models/ObservationLocation.cs - C# model for observation location

### Files to Update
- ServerApi/boxtags.php - Add colony_id support
- ServerApi/config.php - Already has retry logic (no changes needed)
- Services/DataStorageService.cs - Add sync methods
- Pages/SettingsPage.xaml - Add colony selection UI
- Services/DataManager.cs - Track selected colony

---

## Data Migration Details

### Tag Number Format Migration
- Current app uses 8-character IDs (last 8 chars of scanned tag)
- Database will store full 17-character ISO format
- Migration script will need to map existing 8-char IDs to full 17-char format from Google Sheets source data

### Default Values for Historical Data
- **Default Observer**: Create an "unknown" observer for all historical data that predates the observer system
- **Default Colony**: Assign historical data to appropriate colony based on box_sets_string match

### Old Backend System
- The existing `Backend.RequestServerResponse()` system (PenguinReport:/PenguinRequest-Saved:) is being **deprecated**
- No transition period - old system will be replaced entirely
- Data will not be synced between old and new systems

---

## Notes

- All timestamps stored as UTC in database
- GPS coordinates stored as DOUBLE (latitude/longitude) and FLOAT (accuracy)
- Persistent notes stored directly in observation_locations table (no separate table needed)
- Location sets string stored in colonies table using AllBoxSetsString format (can include box numbers, beach names, etc.)
- RFID tags merged into observation_locations table (no separate box_tags table)
- Observer passphrases hashed with bcrypt (password_hash/password_verify in PHP)
- No complexity requirements (trusted users, but securely hashed)
- Penguin tag numbers are 17 chars (full ISO format from scanner)
- Breeding chance and gate status stored as VARCHAR for flexibility
- Soft deletes on observations (is_deleted flag) to enable undo
- Cascade delete on penguin_scans only when observation is hard-deleted (rare)
- Audit log captures all changes with before/after values in JSON format
- Indexes on frequently queried fields (observation_time, location_id, tag_number, observer_id)
- SQL database is source of truth for penguin data (replaces Google Sheets)
- Breeding date predictions calculated client-side (not stored in database)