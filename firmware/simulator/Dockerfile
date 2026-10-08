FROM python:3.12-alpine AS base
WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY *.py ./

FROM base AS test
RUN python -B -m unittest -v test_protocol

FROM base AS runtime
RUN adduser -D -u 10001 simulator && mkdir /state && chown simulator /state
USER simulator
ENTRYPOINT ["python", "-B", "/app/main.py"]
