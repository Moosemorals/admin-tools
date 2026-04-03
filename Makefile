PROJECT     := src/uk.osric.copilot/uk.osric.copilot.csproj
PUBLISH_DIR := publish
IMAGE_NAME  := registry.osric.uk/uk.osric.copilot
IMAGE_TAG   := latest
DATE_TAG    := $(shell date -u +%Y%m%d.%H%M%S)

.PHONY: all build run clean package publish start-copilot

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

## Build the container image locally using the .NET SDK's built-in container publishing
package:
	dotnet publish $(PROJECT) \
		--configuration Release \
		--output $(PUBLISH_DIR) \
		/t:PublishContainer \
		/p:ContainerImageTag=$(IMAGE_TAG)

## Tag the locally-built image with a date-based tag and push both tags to the registry.
## Requires 'package' to have been run first (or runs it as a dependency).
publish: package
	podman push $(IMAGE_NAME):$(IMAGE_TAG)
	podman tag  $(IMAGE_NAME):$(IMAGE_TAG) $(IMAGE_NAME):$(DATE_TAG)
	podman push $(IMAGE_NAME):$(DATE_TAG)

## Start the GitHub Copilot CLI in server mode so the wrapper can connect to it.
## Set CopilotUrl in appsettings.json (or via environment) to the printed URL.
start-copilot:
	gh copilot server
