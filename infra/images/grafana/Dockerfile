# Grafana for Azure Container Apps, provisioned entirely from files baked into
# the image (Container Apps has no bind mounts). Same version as compose.
# Dashboards and the dashboard provider are reused from deploy/grafana, so a
# dashboard edited there ships to Azure with no second copy to keep in sync.
# Only the datasource differs. Build context: repository root.
FROM grafana/grafana:12.1.0

COPY deploy/grafana/provisioning/dashboards /etc/grafana/provisioning/dashboards
COPY infra/images/grafana/datasources.yml /etc/grafana/provisioning/datasources/datasources.yml
COPY deploy/grafana/dashboards /etc/grafana/dashboards
