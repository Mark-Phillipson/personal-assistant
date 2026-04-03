# Voice Admin Entity Relationship Diagram

This diagram reflects the main Voice Admin SQLite tables currently used by this assistant codebase.

- Declared SQLite foreign keys are shown for `Launcher` and `CustomIntelliSense`.
- `Todos -> Categories` is included as a logical application-level relationship because the code joins `Todos.Project` to `Categories.Category`, even though the database does not enforce it as a foreign key.
- `TalonVoiceCommands`, `ValuesToInsert`, and `Transactions` are currently used as standalone lookup/search tables.

```mermaid
erDiagram
    Categories {
        INTEGER ID PK
        TEXT Category
        TEXT Category_Type
        INTEGER Sensitive
        TEXT Colour
        TEXT Icon
    }

    Todos {
        INTEGER Id PK
        TEXT Title
        TEXT Description
        INTEGER Completed
        TEXT Project
        INTEGER Archived
        TEXT Created
        INTEGER SortPriority
    }

    Launcher {
        INTEGER ID PK
        TEXT Name
        TEXT CommandLine
        TEXT WorkingDirectory
        TEXT Arguments
        INTEGER CategoryID FK
        INTEGER ComputerID FK
        TEXT Colour
        TEXT Icon
        INTEGER Favourite
        INTEGER SortOrder
    }

    CustomIntelliSense {
        INTEGER ID PK
        INTEGER LanguageID FK
        TEXT Display_Value
        TEXT SendKeys_Value
        TEXT Command_Type
        INTEGER CategoryID FK
        TEXT Remarks
        TEXT Search
        INTEGER ComputerID FK
        TEXT DeliveryType
    }

    Languages {
        INTEGER ID PK
        TEXT Language
        INTEGER Active
        TEXT Colour
        TEXT ImageLink
    }

    Computers {
        INTEGER ID PK
        TEXT ComputerName
    }

    TalonVoiceCommands {
        INTEGER Id PK
        TEXT Command
        TEXT Script
        TEXT Application
        TEXT Title
        TEXT Mode
        TEXT OperatingSystem
        TEXT FilePath
        TEXT Repository
        TEXT Tags
        TEXT CodeLanguage
        TEXT Language
        TEXT Hostname
        TEXT CreatedAt
    }

    ValuesToInsert {
        INTEGER ID PK
        TEXT ValueToInsert
        TEXT Lookup
        TEXT Description
    }

    Transactions {
        INTEGER Id PK
        TEXT Date
        TEXT Description
        TEXT Type
        DECIMAL MoneyIn
        DECIMAL MoneyOut
        DECIMAL Balance
        TEXT MyTransactionType
        TEXT ImportFilename
        TEXT ImportDate
    }

    Categories ||--o{ Launcher : categorizes
    Computers ||--o{ Launcher : scopes_to

    Categories ||--o{ CustomIntelliSense : groups
    Languages ||--o{ CustomIntelliSense : language
    Computers ||--o{ CustomIntelliSense : computer

    Categories ||--o{ Todos : "logical via Project -> Category"
```

## Notes

- The `Todos` join is implemented in code by matching `lower(trim(Categories.Category))` to `lower(trim(Todos.Project))`.
- `Launcher` and `CustomIntelliSense` are the most relational parts of the Voice Admin schema.
- The assistant also reads `TalonVoiceCommands`, `ValuesToInsert`, and `Transactions` directly for search and lookup workflows, but those tables do not currently expose foreign-key relationships in SQLite.