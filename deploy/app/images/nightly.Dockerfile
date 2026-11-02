# Build context: deploy/app/images. Only curl, the CronJob holds the command.
FROM curlimages/curl:8.14.1
# Numeric, so runAsNonRoot can verify it.
USER 100
