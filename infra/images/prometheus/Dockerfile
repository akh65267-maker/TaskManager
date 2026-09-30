# Prometheus for Azure Container Apps, with its configuration baked in
# (Container Apps has no bind mounts). Same version as deploy/docker-compose.yml.
# Alert rules are reused from deploy/ rather than copied, so there is one
# definition. Build context: repository root.
FROM prom/prometheus:v3.5.0

COPY infra/images/prometheus/prometheus.yml /etc/prometheus/prometheus.yml
COPY deploy/prometheus/rules /etc/prometheus/rules

# Shorter retention than compose (15d): the TSDB is ephemeral here, since
# Prometheus does not support SMB/NFS-backed storage, so long retention buys
# nothing across a restart.
CMD ["--config.file=/etc/prometheus/prometheus.yml", \
     "--storage.tsdb.path=/prometheus", \
     "--storage.tsdb.retention.time=7d"]
