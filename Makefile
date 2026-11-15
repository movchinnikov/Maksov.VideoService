SHELL := /bin/bash
.DEFAULT_GOAL := help

COMPOSE := docker compose --env-file .env -f deploy/compose/docker-compose.yml
SDK_IMAGE := videoservice-dotnet-sdk
WEB := cd clients/web &&

# .NET: each service is a separate solution in its own folder; shared libraries (libs) are NuGet packages from artifacts/nuget.
SOLUTIONS := libs $(patsubst %/,%,$(dir $(wildcard services/*/Maksov.VideoService.*.slnx)))
TEST_PG_PASSWORD := $(shell python3 -c 'import secrets; print(secrets.token_hex(8))')
PG_TEST_URL := Host=videoservice-test-pg;Database=test;Username=postgres;Password=$(TEST_PG_PASSWORD)

# docker run in the solution directory: $(call sdk,<directory>) <command>
sdk = docker run --rm -v "$(CURDIR)":/src -v videoservice-nuget:/nuget -e CI -w /src/$(1) $(SDK_IMAGE)
sln = $(notdir $(firstword $(wildcard $(1)/*.slnx)))

.PHONY: help env-sync up up-ml down logs ps test test-dotnet test-web lint pack demo verify-auth idps sdk-image web-deps clean

help: ## List commands
	@grep -E '^[a-z-]+:.*?## ' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[1m%-12s\033[0m %s\n", $$1, $$2}'

.env:
	./scripts/gen-env.sh

# Creates/updates the local .env from .env.example: adds new variables and generates random secrets locally.
env-sync:
	./scripts/gen-env.sh

up: env-sync ## Bring the stack up from scratch and wait until healthy
	$(COMPOSE) up -d --build --wait --wait-timeout 300
	@echo ""
	@echo "  Web:       http://localhost:3000   (user: demo, password: DEMO_PASSWORD from .env)"
	@echo "  API:       http://localhost:8081   (gateway → videos, users, analysis, comments, friends, feed)"
	@echo "  Keycloak:  http://localhost:8080   (admin — see .env)"
	@echo "  MinIO:     http://localhost:9001"

up-ml: env-sync ## Bring the stack up with a local vision model (Ollama) for frame captioning
	ANALYSIS_CAPTIONER=ollama COMPOSE_PROFILES=ml $(COMPOSE) up -d --build --wait --wait-timeout 300
	$(COMPOSE) exec ollama ollama pull $${OLLAMA_MODEL:-moondream}
	@echo "Frame captioning enabled for new videos (model: $${OLLAMA_MODEL:-moondream})."

down: ## Stop the stack and delete data
	COMPOSE_PROFILES=ml $(COMPOSE) down -v --remove-orphans

logs: ## Service logs (make logs s=media-worker)
	$(COMPOSE) logs -f --tail=200 $(s)

ps: ## Container status
	$(COMPOSE) ps

sdk-image:
	@docker build -q -t $(SDK_IMAGE) -f deploy/docker/dotnet-sdk.Dockerfile deploy/docker >/dev/null

pack: sdk-image ## NuGet packages libs/* into artifacts/nuget (version 0.1.0-local.<source hash>)
	@rm -rf artifacts/nuget
	@HASH=$$(find build libs -type f \( -name '*.cs' -o -name '*.csproj' -o -name '*.props' -o -name '*.targets' \) -not -path '*/bin/*' -not -path '*/obj/*' | LC_ALL=C sort | xargs shasum | shasum | cut -c1-8); \
	suffix="local.$$HASH$(if $(VERSION_SUFFIX),.$(VERSION_SUFFIX))"; \
	echo "library version: 0.1.0-$$suffix"; \
	for p in Primitives BuildingBlocks Web TestSupport; do \
	  $(call sdk,libs) dotnet pack Maksov.VideoService.$$p -c Release -o /src/artifacts/nuget -p:VersionSuffix=$$suffix >/dev/null || exit 1; \
	done; \
	ls -1 artifacts/nuget

web-deps:
	@$(WEB) [ -d node_modules ] || npm ci --no-audit --no-fund

test: test-dotnet test-web ## All tests

test-dotnet: pack ## .NET: tests of each solution (unit, API, integration with ffmpeg and Postgres in a temporary container)
	@docker network create videoservice-test >/dev/null 2>&1 || true
	@docker rm -f videoservice-test-pg >/dev/null 2>&1 || true
	docker run -d --rm --name videoservice-test-pg --network videoservice-test --tmpfs /var/lib/postgresql \
		-e POSTGRES_PASSWORD=$(TEST_PG_PASSWORD) -e POSTGRES_DB=test pgvector/pgvector:0.8.1-pg18 >/dev/null
	@until docker exec videoservice-test-pg pg_isready -U postgres -d test >/dev/null 2>&1; do sleep 1; done
	@status=0; for s in $(SOLUTIONS); do \
	  echo ""; echo "== tests: $$s"; \
	  docker run --rm --network videoservice-test -v "$(CURDIR)":/src -v videoservice-nuget:/nuget -w /src/$$s \
	    -e VIDEOSERVICE_TEST_POSTGRES="$(PG_TEST_URL)" $(SDK_IMAGE) dotnet test --solution $$(basename $$(ls $$s/*.slnx)) || status=1; \
	done; \
	docker rm -f videoservice-test-pg >/dev/null; exit $$status

test-web: web-deps ## Web: vitest
	$(WEB) npm test

lint: pack web-deps ## Analyzers and formatting of all solutions, eslint, tsc
	@for s in $(SOLUTIONS); do \
	  echo "== lint: $$s"; sln=$$(basename $$(ls $$s/*.slnx)); \
	  $(call sdk,$$s) dotnet build $$sln -c Release -warnaserror || exit 1; \
	  $(call sdk,$$s) dotnet format $$sln --verify-no-changes --no-restore || exit 1; \
	done
	$(WEB) npm run lint && npm run typecheck

demo: .env ## End-to-end lesson scenario on a running stack
	./scripts/demo.sh

verify-auth: ## Keycloak login and registration: username, e-mail, phone, redirect_uri
	set -a; source .env; set +a; python3 scripts/verify-auth.py

idps: .env ## Connect Google/Microsoft SSO in Keycloak (from .env)
	set -a; source .env; set +a; ./deploy/keycloak/configure-idps.sh

clean: ## Remove build artifacts
	find . -type d \( -name bin -o -name obj -o -name TestResults \) -prune -exec rm -rf {} +
	rm -rf artifacts clients/web/dist
