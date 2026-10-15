# AIM - AI-powered Git Commit Message Generator

> **A**utomatically **G**enerate **C**ommit **M**essages using **AI**

AIM is a command-line tool that leverages AI to generate meaningful, semantic commit messages based on your Git changes. Say goodbye to generic "update files" commits and hello to clear, professional commit history.

`aim review` uses the same OpenAI-compatible setup to comment on a diff: staged changes by default, or a commit range when you pass refs.

## ✨ Features

- 🤖 **AI-Powered**: Uses OpenAI-compatible APIs to analyze your git diff and generate contextual commit messages
- ⚡ **Fast & Efficient**: Built with .NET 10.0 AOT compilation for blazing-fast performance
- 🎯 **Semantic Commits**: Follows best practices with imperative mood and proper structure
- ⚙️ **Configurable**: Customize model, endpoint, message length, and behavior
- 🔄 **Auto-commit**: Optional automatic commit after message generation
- 📝 **Amend Support**: Generate messages for amending previous commits
- 🔍 **Diff review**: `aim review` prints line comments for staged changes, a triple-dot range, or one commit
- 🛠️ **Optional review agent**: `--agent` can read files and search the repo before it answers. Off unless you ask for it
- 🎨 **Clean Output**: Beautiful, readable console output

## 🚀 Quick Start

### Installation

```bash
# Clone the repository
git clone https://github.com/yourusername/aim.git
cd aim

# Build the project
dotnet build

# Publish as native AOT (optional, for optimal performance)
dotnet publish -c Release
```

### First-Time Setup

1. Configure your OpenAI API key:
```bash
aim config --set apikey=YOUR_API_KEY
```

2. (Optional) Set a custom API endpoint:
```bash
aim config --set apiendpoint=https://api.openai.com/v1
```

3. (Optional) Choose your preferred model:
```bash
aim config --set model=gpt-4
```

### Basic Usage

```bash
# Stage your changes
git add .

# Generate and preview commit message
aim commit

# Or stage all changes and commit in one go
aim commit --all

# Auto-commit without manual confirmation
aim config --set autocommit=true
aim commit
```

## 📖 Commands

### `aim commit`

Generate AI-powered commit messages for staged changes.

**Options:**
- `-a, --all`: Stage all changes (`git add .`) before generating message
- `--amend`: Generate message for amending the last commit

**Examples:**
```bash
# Generate message for staged changes
aim commit

# Stage everything and generate message
aim commit --all

# Generate message to amend last commit
aim commit --amend
```

### `aim review`

Review a diff and print AI line comments. The default input is staged changes (`git diff --cached`), the same set `aim commit` reads.

**Options:**
- `--from <ref>` / `--to <ref>`: review the triple-dot range. That is the changes on `--to` since it diverged from `--from`. The two flags must be passed together.
- `--commit <rev>`: review the patch of one commit. Cannot be combined with `--from` / `--to`.
- `--rule <file>`: extra rules text, appended to the built-in checklist.
- `--background "..."`: extra product context for the model.
- `--format text|json`: human-readable text (default) or a JSON array of comments.
- `--agent`: opt in to a Microsoft Agent Framework review agent (`ChatClientAgent`) that may call `file_read` and `code_search` before the final answer. Off by default. The final answer is still the same JSON comment array.
- `--max-rounds <n>`: cap agent tool rounds (1–40). Overrides `maxreviewrounds`. Without `--agent`, a valid value is ignored after a note on stderr.

Line numbers refer to the **new file** in the diff. Aim writes those numbers onto the diff before calling the model. It does not relocate comments afterward, so a line that does not match the file on disk is best-effort.

When the diff is longer than `maxdiffchars`, Aim warns and sends a prefix of the patch, keeping whole files when they fit. If the budget cannot hold a hunk, it falls back to file names (the same idea as `aim commit`). `diffnameonly=true` sends only file names.

Each comment has `path`, `start_line`, `end_line`, `severity` (`critical`, `high`, `medium`, `low`), `category` (`bug`, `security`, `performance`, `maintainability`, `style`, `other`), `content`, and an optional `suggestion_code`. At most `maxreviewcomments` comments are printed (default 20). Set `reviewmodel` when review should use a different model than `model`.

With `--agent`, the agent may call two tools before that JSON array:

- `file_read` reads a repo-relative file. For `--commit`, or for `--from`/`--to`, it uses `git show` of that commit or the `--to` ref. A staged review reads the working tree. Paths must stay inside the repository. A read returns at most 200 lines.
- `code_search` runs `git grep` for a fixed string (not a shell command and not a regular expression) and returns at most 80 lines.

Tool progress is printed on stderr (`▶ file_read …`, `▶ code_search …`). Tool text is truncated to `maxtoolresultchars` (default 12000). Agent rounds default to `maxreviewrounds` (10).

An empty diff prints a message and exits with code 1. A review that finds nothing to say exits 0.

**Examples:**
```bash
# Review staged changes
aim review

# Review commits on feature since it diverged from main
aim review --from main --to feature

# Review one commit
aim review --commit HEAD

# JSON for scripts
aim review --format json

# Let the agent read files and search before answering
aim review --agent --max-rounds 8

# Product context and a local rules file
aim review --background "Payments service. Prefer explicit errors." --rule ./REVIEW.md
```

### `aim config`

Manage configuration settings.

**Options:**
- `--show`: Display current configuration (default)
- `--set key=value`: Set a configuration value
- `--reset`: Reset to default configuration

**Configurable Settings:**

| Key | Description | Default                     |
|-----|-------------|-----------------------------|
| `apikey` | OpenAI API key | _(required)_                |
| `apiendpoint` | API endpoint URL | `https://api.openai.com/v1` |
| `model` | AI model to use | `gpt-4o`                    |
| `autocommit` | Auto-commit after generation | `false`                     |
| `maxsubjectlength` | Max characters in subject line | `72`                        |
| `diffnameonly` | Show only file names in diff | `false`                     |
| `maxdiffchars` | Max diff size before truncation or name-only fallback (`0` = no limit) | `80000` |
| `reviewmodel` | Model for `aim review`. Empty uses `model` | _(empty)_ |
| `maxreviewcomments` | Maximum review comments to print | `20` |
| `maxreviewrounds` | Maximum `--agent` tool rounds | `10` |
| `maxtoolresultchars` | Maximum characters returned by one review tool | `12000` |

**Examples:**
```bash
# View current config
aim config --show

# Set API key
aim config --set apikey=sk-...

# Set multiple values at once
aim config --set apikey=sk-... model=gpt-4o maxdiffchars=80000

# Use a separate model for review and cap the comment count
aim config --set reviewmodel=gpt-4o maxreviewcomments=20 maxreviewrounds=10 maxtoolresultchars=12000

# Or repeat --set
aim config --set apikey=sk-... --set model=gpt-4o --set autocommit=true

# Enable auto-commit
aim config --set autocommit=true

# Use GPT-4
aim config --set model=gpt-4

# Reset to defaults
aim config --reset
```

## ⚙️ Configuration File

Configuration is stored in:
- **Windows**: `%APPDATA%\aim\config.ini`
- **Linux/Mac**: `~/.config/aim/config.ini`

Example `config.ini`:
```ini
[General]
ApiKey = your-api-key-here
ApiEndpoint = https://api.openai.com/v1
Model = gpt-4

[Behavior]
AutoCommit = false
AutoPush = false

[Rules]
MaxSubjectLength = 72
DiffNameOnly = false
MaxDiffChars = 80000

[Review]
ReviewModel =
MaxReviewComments = 20
MaxReviewRounds = 10
MaxToolResultChars = 12000
```

## 🎯 Commit Message Rules

AIM generates commit messages following these conventions:

- ✅ **Imperative mood**: "Fix bug" not "Fixed bug"
- ✅ **Concise subject**: Under 72 characters by default
- ✅ **Semantic structure**: Clear subject line with optional detailed body
- ✅ **Issue references**: Automatically includes issue numbers when detected
- ✅ **Proper formatting**: Subject line separated from body by blank line

## 🔧 Technology Stack

- **.NET 10.0**: Latest .NET with native AOT compilation
- **OpenAI SDK**: Official OpenAI .NET library (v2.14.0)
- **Microsoft Agent Framework**: `ChatClientAgent` for the optional `aim review --agent` tool loop (v1.25.0). Microsoft.Extensions.AI is the chat client underneath that agent.
- **System.CommandLine**: Modern command-line parsing (v2.0.0)
- **Microsoft.Extensions.Configuration**: Robust configuration management

## 📋 Requirements

- .NET 10.0 SDK or runtime
- Git installed and available in PATH
- OpenAI API key or compatible API endpoint

## 🤝 Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add some amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## 📝 License

This project is open source. Please check the LICENSE file for details.

## 🙏 Acknowledgments

- Built with [OpenAI](https://openai.com/) API
- Powered by [.NET](https://dot.net/)
- Inspired by the need for better commit messages everywhere

## 🐛 Troubleshooting

### "No changes to commit" / "No staged changes to review"
Make sure you've staged your changes with `git add` first, or use `aim commit --all`. For review, you can also pass `--from` and `--to`, or `--commit`.

### "API key is required"
Set your API key with: `aim config --set apikey=YOUR_KEY`

### "Error calling AI API"
Check your API endpoint and ensure your API key is valid. Verify network connectivity.

---

**Made with ❤️ by [huiyuanai709](https://github.com/huiyuanai709)**

*Stop writing boring commit messages. Let AI do it for you.* 🚀