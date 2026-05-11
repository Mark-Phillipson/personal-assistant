Hello,

I can convert your two Excel workbooks into a simple, reliable Blazor (.NET) web app that reproduces and improves the calculations and reports you currently run in Excel. The app will: import the existing Excel files (or accept CSV uploads), consolidate order data for the partner packaging team, compute packaging units and cartons required per order, and run staged cost rollups that match your spreadsheet logic (goods shipped vs order, manufactured/packaged, allocation of general vs product-specific costs, pre-/post-freight-forwarder, CAF, port-in-Canada, storage in Montreal after customs and transport).

Key features:
- Excel import/export and data validation so current workflows keep working while users test the app.
- Calculation engine that reproduces your spreadsheets exactly and supports scenario simulations (margins at 30%, 35%, 40%; market price vs our price vs best-client price) with comparative dashboards.
- Cost allocation module to split general and specific costs across products and show stage-by-stage P&L and margins.
- Interactive dashboard with filters, per-order and aggregated views, and downloadable reports (Excel/PDF/CSV).
- Role-based access (basic) and simple cloud hosting/deployment (Azure App Service) plus documentation and unit tests for core calculations.
 
- Single source of truth: a centralized calculation engine and validated data store so every user, report, and export use identical, auditable results.

Recommended tech:
- Option A (recommended): Blazor front-end (Blazor Server by default; Blazor WebAssembly if you need offline client-side execution) + .NET 10 backend — same language across stack, easy reuse of .NET calculation code, and strong Visual Studio tooling. SQL Server or SQLite for storage; Azure App Service (cloud) hosting.

Why I recommend Blazor instead of a JavaScript framework:
- Single-language stack: UI, validation, and business logic in C# avoids context switching and reduces integration friction.
- Easier porting of Excel logic: .NET libraries (ExcelDataReader, EPPlus/ClosedXML) and existing C# code can be reused for exact calculation reproduction and reliable unit tests.
- Strong tooling and debugging: Visual Studio/VS Code support, hot reload, and server-side debugging speed up development and reduce bugs.
- Enterprise integration: straightforward access to .NET data access libraries, validation frameworks, and secure hosting on Azure App Service.
- Lower JavaScript overhead: less client-side JS to maintain; Blazor Server keeps the UI responsive with small payloads and server-run calculations when needed.

Deliverables & timeline:
- Discovery & sample analysis: review Excel files, confirm edge cases and mapping — 1–2 days after files provided.
- MVP (import + core calculations + basic report + unit tests): ~1–2 weeks after access to sample Excel files and clarification of rules.
- Full app (dashboard, scenario simulations, exports, testing, deployment): typically within 3 weeks.
- Deployment & documentation: Azure App Service configuration, runbook, and short onboarding notes.

Estimate & next steps:
- Rough estimate: 20–50 hours depending on final scope. I can provide a fixed-price quote after reviewing the actual Excel files and confirming edge cases.
- Please share the two Excel files (with any sample orders and a notes sheet if available) and any user access/hosting constraints; a short 20–30 minute call or recorded walkthrough of the spreadsheets will speed delivery.

Why hire me:
- Experience converting complex Excel models into maintainable web apps, with clear unit-tested calculation logic and simple dashboards so business users trust results.

If this sounds good, share the files and a preferred hourly rate or budget and availability for a quick call and work can start immediately.

Best regards,
Mark P.
