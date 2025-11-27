# AIM - AI-powered Git Commit Message Generator

> **A**utomatically **G**enerate **C**ommit **M**essages using **AI**

AIM is a command-line tool that leverages AI to generate meaningful, semantic commit messages based on your Git changes. Say goodbye to generic "update files" commits and hello to clear, professional commit history.

## ✨ Features

- 🤖 **AI-Powered**: Uses OpenAI-compatible APIs to analyze your git diff and generate contextual commit messages
- ⚡ **Fast & Efficient**: Built with .NET 10.0 AOT compilation for blazing-fast performance
- 🎯 **Semantic Commits**: Follows best practices with imperative mood and proper structure
- ⚙️ **Configurable**: Customize model, endpoint, message length, and behavior
- 🔄 **Auto-commit**: Optional automatic commit after message generation
- 📝 **Amend Support**: Generate messages for amending previous commits
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

**Examples:**
```bash
# View current config
aim config --show

# Set API key
aim config --set apikey=sk-...

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
- **OpenAI SDK**: Official OpenAI .NET library (v2.7.0)
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

### "No changes to commit"
Make sure you've staged your changes with `git add` first, or use `aim commit --all`.

### "API key is required"
Set your API key with: `aim config --set apikey=YOUR_KEY`

### "Error calling AI API"
Check your API endpoint and ensure your API key is valid. Verify network connectivity.

---

**Made with ❤️ by [huiyuanai709](https://github.com/huiyuanai709)**

*Stop writing boring commit messages. Let AI do it for you.* 🚀