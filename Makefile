PROJECT     := src/CopilotWrapper/CopilotWrapper.csproj
PUBLISH_DIR := publish
IMAGE_NAME  := copilot-wrapper
IMAGE_TAG   := latest

.PHONY: all build run clean package start-copilot

## Default target
all: build

## Restore dependencies and compile the project
build:
	dotnet build $(PROJECT)

## Run the application (development mode)
run:
	dotnet run --project $(PROJECT)

## Remove all build and publish artifacts
clean:
	dotnet clean $(PROJECT)
	rm -rf $(PUBLISH_DIR)

## Publish a self-contained release build and create a container image
## Uses the .NET SDK's built-in container publishing (no Dockerfile required)
package:
	dotnet publish $(PROJECT) \
		--configuration Release \
		--output $(PUBLISH_DIR) \
		/t:PublishContainer \
		/p:ContainerImageName=$(IMAGE_NAME) \
		/p:ContainerImageTag=$(IMAGE_TAG)

## Start the GitHub Copilot CLI in server mode so the wrapper can connect to it.
## Set COPILOT_URL in appsettings.json (or via environment) to the printed URL.
start-copilot:
	gh copilot server
