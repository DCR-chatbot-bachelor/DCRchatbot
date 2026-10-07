# DCRchatbot

## Local setup

Requirements:

- .NET 10 SDK
- Node.js and npm

The Web API uses ASP.NET Core configuration. Non-secret defaults are stored in
`src/DcrChatbot.WebApi/appsettings.json`. API keys and tokens must be stored in
the local .NET User Secrets store and must never be committed to git.

Initialize User Secrets once from the repository root:

```powershell
dotnet user-secrets init --project .\src\DcrChatbot.WebApi
dotnet user-secrets set "Llm:ApiKey" "your-gemini-api-key" --project .\src\DcrChatbot.WebApi
dotnet user-secrets set "Llm:ModelId" "your-model-id" --project .\src\DcrChatbot.WebApi
dotnet user-secrets set "Dcr:ApiKey" "your-api-key" --project .\src\DcrChatbot.WebApi
dotnet user-secrets set "Dcr:Token" "your-bearer-token" --project .\src\DcrChatbot.WebApi
```

Set the DCR URL in `appsettings.json` or override it locally:

```powershell
dotnet user-secrets set "Dcr:RootUrl" "https://your-dcr-host/" --project .\src\DcrChatbot.WebApi
```

The DCR configuration requires all three values: `Dcr:RootUrl`, `Dcr:ApiKey`,
and `Dcr:Token`. If startup reports that `DcrOptions.ApiKey` is required, set
the missing DCR API key in User Secrets:

```powershell
dotnet user-secrets set "Dcr:ApiKey" "your-dcr-api-key" --project .\src\DcrChatbot.WebApi
```

You can verify that the required secret names exist without printing their
values:

```powershell
dotnet user-secrets list --project .\src\DcrChatbot.WebApi
```

Start the API:

```powershell
dotnet run --project .\src\DcrChatbot.WebApi
```

Install and start the client in a second terminal:

```powershell
Push-Location .\client
npm install
npm run dev
Pop-Location
```

The API uses the URL shown by ASP.NET Core, normally `http://localhost:5169`.
The Vite client normally runs at `http://localhost:5173`.

## Session storage

Chat sessions are persisted as JSON files below the API's `App_Data/sessions`
directory and each session operation is serialized. This supports restarts for
a single API instance. Deployments with multiple API instances must replace
`JsonFileSessionStore` with a shared store such as Redis or a database.
