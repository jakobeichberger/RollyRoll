FROM python:3.12-slim

LABEL org.opencontainers.image.title="Schul-Monitoring Registrierungs-Dienst"
LABEL org.opencontainers.image.description="Nimmt Agenten-Anmeldungen entgegen und erzeugt daraus die Prometheus-Ziellisten"

ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY app.py .

# Nicht als root laufen lassen
RUN useradd --system --uid 10200 --create-home registrar \
 && mkdir -p /state /targets \
 && chown -R registrar:registrar /state /targets /app
USER registrar

EXPOSE 8000

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD python -c "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8000/api/v1/health', timeout=4).status==200 else 1)"

CMD ["python", "app.py"]
