FROM node:22-slim
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --omit=dev
COPY src ./src
ENV PORT=8787
EXPOSE 8787
CMD ["npx", "tsx", "src/server.ts"]
