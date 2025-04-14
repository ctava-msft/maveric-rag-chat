# Overview
This project provides scripts for RAG chat at the command line in c#.

# Instructions

Deploy infra using the following commands:
```bash
azd auth login
azd up
```

Copy sample.env to .env.
Fill in the <redacted> values.

In order, run the scripts using the following command(s):


```
dotnet run --project ./Create.csproj
```
Add semantic configuration in search index by hand.

```
dotnet run --project ./Ingest.csproj
```

```
dotnet run --project ./Query.csproj
```