# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Projects** — a user-owned container that groups conversations and gives them a shared document set and standing instructions. Manage projects at `api/projects`, upload documents into one with `POST api/documents/projects/{projectId}` (same pipeline and upload-status route as conversation uploads), and place a conversation in a project with `projectId` on conversation create or update. A project's instructions are read fresh on every turn and applied as request-level instructions, so editing them affects conversations that already exist without altering their transcripts. Deleting a project removes its documents but keeps its conversations, which become standalone. Note two things before adopting it: `PUT api/conversations` replaces the whole conversation, so an omitted `projectId` removes it from its project; and retrieval over project documents is not implemented yet, so uploaded documents are indexed but do not yet influence answers. The new `Core.Project`, `Core.ProjectDocument` and `Core.ProjectDocumentChunk` tables and the `Core.Conversation.ProjectId` column ship as EF model changes only — there is no migration and no DDL script, so they must be applied to each real database out of band before the feature can be used (SQL Server 2025 or later). See [docs/projects/project-management.md](docs/projects/project-management.md).
