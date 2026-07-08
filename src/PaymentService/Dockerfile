# Use the official .NET 8 SDK image for building
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Copy the project file and restore dependencies
COPY *.csproj ./
RUN dotnet restore

# Copy the remaining source code and publish
COPY . .
RUN dotnet publish -c Release -o out

# Build the runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
RUN apt-get update && apt-get install -y wget curl ca-certificates && rm -rf /var/lib/apt/lists/*
# Install a fixed Dapr CLI version and runtime to avoid installer parsing issues
RUN wget -q https://github.com/dapr/cli/releases/download/v1.13.0/dapr_linux_amd64.tar.gz -O /tmp/dapr-cli.tar.gz \
 	 && set -eux; \
		 wget -O /tmp/dapr-cli.tar.gz https://github.com/dapr/cli/releases/download/v1.13.0/dapr_linux_amd64.tar.gz; \
		 tar -xzf /tmp/dapr-cli.tar.gz -C /tmp; \
		 for f in /tmp/dapr*; do mv "$f" /usr/local/bin/; done; \
		 chmod +x /usr/local/bin/dapr*; \
		 rm -f /tmp/dapr-cli.tar.gz; \
		 \
		 # Download Dapr runtime (daprd) and place it where the CLI expects it
		 wget -q https://github.com/dapr/dapr/releases/download/v1.13.0/daprd_linux_amd64.tar.gz -O /tmp/dapr-runtime.tar.gz; \
		 tar -xzf /tmp/dapr-runtime.tar.gz -C /tmp; \
		 mkdir -p /usr/local/bin/.dapr/bin; \
		 if [ -f /tmp/daprd ]; then mv /tmp/daprd /usr/local/bin/.dapr/bin/daprd; fi; \
		 chmod +x /usr/local/bin/.dapr/bin/daprd; \
		 # Provide a minimal Dapr config so daprd can start
		 mkdir -p /usr/local/bin/.dapr; \
		 echo 'apiVersion: dapr.io/v1alpha1' > /usr/local/bin/.dapr/config.yaml; \
		 echo 'kind: Configuration' >> /usr/local/bin/.dapr/config.yaml; \
		 echo 'metadata:' >> /usr/local/bin/.dapr/config.yaml; \
		 echo '  name: default' >> /usr/local/bin/.dapr/config.yaml; \
		 echo 'spec:' >> /usr/local/bin/.dapr/config.yaml; \
		 echo '  mtls:' >> /usr/local/bin/.dapr/config.yaml; \
		 echo '    enabled: false' >> /usr/local/bin/.dapr/config.yaml; \
		 rm -f /tmp/dapr-runtime.tar.gz
COPY --from=build /app/out .

# Expose the port
EXPOSE 8080

ENTRYPOINT ["dotnet", "PaymentService.dll"]
