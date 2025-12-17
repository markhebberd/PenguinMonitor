# PenguinMonitor Database Schema

## Overview

MySQL database schema for penguin colony monitoring. Supports multiple regions and colonies with shared access among trusted observers.

---

## Entity Relationships

```
                          ┌───────────┐
                          │ observers │
                          └─────┬─────┘
                                │
             ┌──────────────────┼──────────────────┐
             │                  │                  │
             ▼                  ▼                  ▼
   ┌────────────────────┐ ┌───────────┐    ┌──────────────┐
   │ colony_permissions │ │ audit_log │    │ observations │
   └──────────┬─────────┘ └───────────┘    └───────┬──────┘
              │                                    │
              ▼                                    │ 1:N
        ┌─────────┐       ┌──────────┐             │
        │ regions │──1:N──│ colonies │             ▼
        └─────────┘       └────┬─────┘    ┌───────────────┐   ┌────────────────────────┐
                               │          │ penguin_scans │   │ penguin_biometric_data │
                               │ 1:N      └───────┬───────┘   └────────────┬───────────┘
                               ▼                  │                        │
              ┌───────────────────────┐           │      FK                │
              │ observation_locations │           └───────────┬────────────┘
              └───────────────────────┘                       ▼
                                                       ┌──────────┐
                                                       │ penguins │
                                                       └──────────┘
```

**Key relationships:**
- Region → Colony (1:N)
- Colony → Observation Location (1:N)
- Observation Location → Observation (1:N)
- Observation → Penguin Scan (1:N)
- Penguin → Penguin Biometric Data (1:N)

---

## Tables

### observers
| Column | Type | Constraints |
|--------|------|-------------|
| observer_id | INT | PK, AUTO_INCREMENT |
| observer_name | VARCHAR(100) | NOT NULL, UNIQUE |
| email | VARCHAR(255) | |
| passphrase_hash | VARCHAR(255) | NOT NULL |
| created_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP |
| updated_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP ON UPDATE |

### regions
| Column | Type | Constraints |
|--------|------|-------------|
| region_id | INT | PK, AUTO_INCREMENT |
| region_name | VARCHAR(100) | NOT NULL, UNIQUE |
| created_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP |
| updated_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP ON UPDATE |

### colonies
| Column | Type | Constraints |
|--------|------|-------------|
| colony_id | INT | PK, AUTO_INCREMENT |
| region_id | INT | NOT NULL, FK → regions |
| colony_name | VARCHAR(100) | NOT NULL |
| location_sets_string | TEXT | Format: `{1-150,AA-AC},{N1-N6}` |

### colony_permissions
| Column | Type | Constraints |
|--------|------|-------------|
| permission_id | INT | PK, AUTO_INCREMENT |
| colony_id | INT | NOT NULL, FK → colonies |
| observer_id | INT | NOT NULL, FK → observers |
| role | VARCHAR(20) | NOT NULL — `admin`, `edit`, `view` |
| created_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP |

### observation_locations
| Column | Type | Constraints |
|--------|------|-------------|
| location_id | INT | PK, AUTO_INCREMENT |
| colony_id | INT | NOT NULL, FK → colonies |
| location_name | VARCHAR(50) | NOT NULL |
| location_type | VARCHAR(20) | DEFAULT 'box' — `box`, `beach`, `burrow`, etc. |
| persistent_notes | TEXT | |
| rfid_tag_number | VARCHAR(50) | |
| rfid_scan_time_utc | DATETIME | |
| rfid_latitude | DOUBLE | |
| rfid_longitude | DOUBLE | |
| rfid_accuracy | FLOAT | |
| created_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP |
| updated_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP ON UPDATE |

### observations
| Column | Type | Constraints |
|--------|------|-------------|
| observation_id | INT | PK, AUTO_INCREMENT |
| location_id | INT | NOT NULL, FK → observation_locations |
| observer_id | INT | NOT NULL, FK → observers |
| observation_time_utc | DATETIME | NOT NULL |
| adults | INT | DEFAULT 0 |
| eggs | INT | DEFAULT 0 |
| chicks | INT | DEFAULT 0 |
| breeding_status | VARCHAR(50) | |
| gate_status | VARCHAR(50) | |
| notes | TEXT | |
| is_deleted | BOOLEAN | DEFAULT FALSE |
| deletion_reason | TEXT | |
| deleted_at | TIMESTAMP | |
| deleted_by | INT | FK → observers |
| created_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP |
| updated_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP ON UPDATE |

### penguins
| Column | Type | Constraints |
|--------|------|-------------|
| penguin_id | INT | PK, AUTO_INCREMENT |
| tag_number | VARCHAR(17) | NOT NULL, UNIQUE — full ISO format |
| chip_date | DATE | |
| chipped_as_adult | BOOLEAN | DEFAULT FALSE |
| sex | VARCHAR(10) | |
| life_stage | VARCHAR(20) | |
| vid_for_scanner | VARCHAR(50) | |
| created_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP |
| updated_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP ON UPDATE |

### penguin_scans
| Column | Type | Constraints |
|--------|------|-------------|
| scan_id | INT | PK, AUTO_INCREMENT |
| observation_id | INT | NOT NULL, FK → observations ON DELETE CASCADE |
| penguin_id | INT | NOT NULL, FK → penguins |
| scan_time_utc | DATETIME | NOT NULL |
| latitude | DOUBLE | |
| longitude | DOUBLE | |
| accuracy | FLOAT | |
| created_at | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP |

### penguin_biometric_data
| Column | Type | Constraints |
|--------|------|-------------|
| biometric_id | INT | PK, AUTO_INCREMENT |
| penguin_id | INT | NOT NULL, FK → penguins |
| observation_id | INT | FK → observations ON DELETE SET NULL |
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

### audit_log
| Column | Type | Constraints |
|--------|------|-------------|
| audit_id | INT | PK, AUTO_INCREMENT |
| table_name | VARCHAR(50) | NOT NULL |
| record_id | INT | NOT NULL |
| action | VARCHAR(20) | NOT NULL — `INSERT`, `UPDATE`, `DELETE`, `UNDELETE` |
| observer_id | INT | NOT NULL, FK → observers |
| changed_fields | JSON | `{field: {old: x, new: y}}` |
| change_timestamp | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP |

---

## Indexes and Constraints

### Unique Constraints
| Table | Columns | Purpose |
|-------|---------|---------|
| colonies | (region_id, colony_name) | Colony names unique within region |
| colony_permissions | (colony_id, observer_id) | One permission per observer per colony |
| observation_locations | (colony_id, location_name) | Location names unique within colony |
| observation_locations | (colony_id, rfid_tag_number) | RFID tags unique within colony |

### Indexes
| Table | Columns | Purpose |
|-------|---------|---------|
| colony_permissions | observer_id | Find colonies for an observer |
| observation_locations | location_type | Filter by location type |
| observations | observation_time_utc | Time-based queries |
| observations | (location_id, observation_time_utc) | Location history |
| observations | observer_id | Find observations by observer |
| observations | is_deleted | Exclude soft-deleted records |
| penguin_scans | observation_id | Scans for an observation |
| penguin_scans | penguin_id | Observation history for a penguin |
| penguin_biometric_data | (penguin_id, observation_date) | Biometric history for a penguin |
| audit_log | (table_name, record_id) | Change history for a record |
| audit_log | observer_id | Changes by an observer |
| audit_log | change_timestamp | Recent changes |

---

## Design Decisions

### Multi-Colony Architecture
- All colonies share one database, isolated by `colony_id` foreign keys
- Each colony can have its own "Box 1" without conflict
- Permissions granted per colony (admin/edit/view roles)

### Location Flexibility
- `observation_locations` supports multiple types: nest boxes, beaches, burrows, rocky areas
- RFID tag data stored directly on location (no separate tags table)
- Locations created automatically on first observation

### Audit and Recovery
- Observations use soft delete (`is_deleted` flag) to enable undo
- `audit_log` tracks all changes with before/after values in JSON
- Every modification linked to an observer for accountability

### Data Types
- All timestamps stored as UTC
- GPS: latitude/longitude as DOUBLE, accuracy as FLOAT
- Penguin tag numbers: 17-character ISO format
- `breeding_status` and `gate_status` as VARCHAR for flexibility

### Derived Data
- Breeding date predictions calculated client-side, not stored
- Database is source of truth for penguin master data
